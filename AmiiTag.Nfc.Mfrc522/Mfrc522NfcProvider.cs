namespace AmiiTag.Nfc.Mfrc522;

/// <summary>
/// NFC through an MFRC522 wired straight to a USB-serial adapter (the chip's UART interface, no microcontroller or
/// firmware). The app drives the chip's registers itself. The chip starts at 9600 baud after power-up; the provider
/// finds it at whichever rate it's running and switches it to <see cref="BaudRate"/> for speed.
/// </summary>
public sealed class Mfrc522NfcProvider : INfcProvider, INfcReaderRequester, INfcReleasable, INfcBaudRateSetting, IAsyncDisposable
{
    /// <summary>
    /// The MFRC522's UART rate after power-up or reset.
    /// </summary>
    public const int PowerOnBaudRate = 9600;

    /// <summary>
    /// The rate the chip is switched to by default: its fastest common UART rate. Adapters that can't keep up fall
    /// back to the rate the chip was found at, and Settings can pick a slower one.
    /// </summary>
    public const int FastBaudRate = 921600;

    /// <summary>
    /// Rates offered in Settings (all supported by the chip's SerialSpeedReg).
    /// </summary>
    public static readonly IReadOnlyList<int> SettingsBaudRates = [115200, 230400, 460800, 921600];

    private readonly ISerialPortFactory _ports;
    private readonly SemaphoreSlim _openLock = new(1, 1);
    private string? _openPortId;
    private Mfrc522Reader? _reader;

    public Mfrc522NfcProvider(ISerialPortFactory ports)
    {
        _ports = ports;
    }

    private int _baudRate = FastBaudRate;

    /// <summary>
    /// The rate the chip is switched to once found (one of <see cref="Mfrc522Reg.SerialSpeeds"/>). Takes effect the next
    /// time the port is opened (see <see cref="ReleaseAsync"/>).
    /// </summary>
    public int BaudRate
    {
        get => _baudRate;
        set => _baudRate = Mfrc522Reg.SerialSpeeds.ContainsKey(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), $"The MFRC522 can't run at {value} baud.");
    }

    int INfcBaudRateSetting.DefaultBaudRate => FastBaudRate;

    public IReadOnlyList<int> SupportedBaudRates => SettingsBaudRates;

    /// <summary>
    /// Only Web Serial needs a port to be granted; desktop COM ports are all listed already.
    /// </summary>
    public bool CanRequestReader => _ports is ISerialPortRequester;

    public string Id => "mfrc522-serial";

    public string DisplayName => "MFRC522 (serial / COM port)";

    public bool IsSupported => _ports.IsSupported;

    public bool ShowsSystemPrompt => false;

    public bool SupportsReaderSelection => true;

    /// <summary>
    /// VersionReg of the connected chip, once found.
    /// </summary>
    public byte? ChipVersion
    {
        get; private set;
    }

    public Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default) =>
        _ports.GetPortsAsync(cancellationToken);

    public async Task<NfcReaderInfo?> RequestReaderAsync(CancellationToken cancellationToken = default)
    {
        if (_ports is ISerialPortRequester requester)
        {
            return await requester.RequestPortAsync(cancellationToken);
        }

        return null;
    }

    public async Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default)
    {
        var portId = await ResolvePortAsync(options.ReaderId, options.AllowUserPrompts, cancellationToken);
        var reader = await GetReaderAsync(portId, cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mfrc522Card? card;
            try
            {
                card = await reader.SelectAsync(cancellationToken);
            }
            catch (NfcException)
            {
                // A garbled answer while a tag is being placed (or two tags at once): keep looking.
                card = null;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await ResetAsync();
                throw new NfcException("Lost contact with the MFRC522.", ex);
            }

            if (card != null)
            {
                return new Mfrc522TagConnection(reader, card);
            }

            // Nothing answered. A tag that's present but stuck (halted by the last session, interrupted mid-command)
            // wouldn't either, so power cycle the field before trying again.
            try
            {
                await reader.ResetFieldAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or NfcException)
            {
            }

            await Task.Delay(150, cancellationToken);
        }
    }

    /// <summary>
    /// Uses the chosen port if it's still available, otherwise the first one. In the browser, when no port has been
    /// granted yet, this opens the Web Serial picker (it must run during the user's click).
    /// </summary>
    private async Task<string> ResolvePortAsync(string? preferred, bool allowPrompt, CancellationToken cancellationToken)
    {
        var ports = await GetReadersAsync(cancellationToken);
        if (!string.IsNullOrEmpty(preferred) && ports.Any(p => p.Id == preferred))
        {
            return preferred;
        }

        if (ports.Count > 0)
        {
            return ports[0].Id;
        }

        if (_ports is ISerialPortRequester requester && allowPrompt)
        {
            var requested = await requester.RequestPortAsync(cancellationToken);
            if (requested == null)
            {
                // The user closed the port picker.
                throw new OperationCanceledException();
            }

            return requested.Id;
        }

        throw new NfcException("No serial ports were found. Plug in the MFRC522 reader and try again.");
    }

    private async Task<Mfrc522Reader> GetReaderAsync(string portId, CancellationToken cancellationToken)
    {
        await _openLock.WaitAsync(cancellationToken);
        try
        {
            if (_reader != null && _openPortId == portId)
            {
                if (await CheckReaderAsync(_reader, cancellationToken))
                {
                    return _reader;
                }

                // The port closed (unplugged) or the chip lost power (back at 9600 baud): open and find it again.
                System.Diagnostics.Debug.WriteLine($"MFRC522: {portId} no longer answers; reopening.");
            }

            await ResetCoreAsync();
            var reader = await FindChipAsync(portId, cancellationToken);
            try
            {
                await reader.InitAsync(cancellationToken);
            }
            catch
            {
                await reader.Uart.DisposeAsync();
                throw;
            }
            _reader = reader;
            _openPortId = portId;
            return reader;
        }
        finally
        {
            _openLock.Release();
        }
    }

    /// <summary>
    /// Checks a reader that's already open still works before reusing it. A chip that answers but has reset (antenna
    /// off after a power blip at the same baud rate) is set up again; one that doesn't answer needs reopening.
    /// </summary>
    private static async Task<bool> CheckReaderAsync(Mfrc522Reader reader, CancellationToken cancellationToken)
    {
        try
        {
            var registers = await reader.Uart.ReadManyAsync([Mfrc522Reg.Version, Mfrc522Reg.TxControl], cancellationToken);
            if (!Mfrc522Reg.KnownVersions.ContainsKey(registers[0]))
            {
                return false;
            }

            if ((registers[1] & Mfrc522Reg.AntennaOn) != Mfrc522Reg.AntennaOn)
            {
                await reader.InitAsync(cancellationToken);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Timeout, closed port (IOException/InvalidOperationException), bad acknowledgement...
            System.Diagnostics.Debug.WriteLine($"MFRC522: open port check failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Looks for the chip at the target rate (it keeps that rate until it loses power), then at the power-on rate,
    /// switching it up if found there.
    /// </summary>
    private async Task<Mfrc522Reader> FindChipAsync(string portId, CancellationToken cancellationToken)
    {
        // The chosen rate first (the chip keeps it until it loses power), then the power-on rate, then any other rate
        // a previous session or setting may have left it at.
        var rates = new[] { BaudRate, PowerOnBaudRate }
            .Concat(new[] { 921600, 460800, 230400, 115200, 57600, 38400, 19200 })
            .Distinct()
            .ToArray();
        foreach (var rate in rates)
        {
            Mfrc522Reader? reader = null;
            try
            {
                reader = await OpenAsync(portId, rate, cancellationToken);
                var version = await reader.GetVersionAsync(cancellationToken);
                System.Diagnostics.Debug.WriteLine($"MFRC522: {portId} at {rate} baud -> VersionReg {(version is byte v ? v.ToString("X2") : "no answer")}");
                if (version == null || !Mfrc522Reg.KnownVersions.ContainsKey(version.Value))
                {
                    continue;
                }

                if (rate != BaudRate && Mfrc522Reg.SerialSpeeds.TryGetValue(BaudRate, out var speed))
                {
                    // SwitchBaudRateAsync takes ownership of the current stream, even if cancelled.
                    var switching = reader;
                    reader = null;
                    var faster = await SwitchBaudRateAsync(switching, portId, speed, cancellationToken);
                    if (faster != null)
                    {
                        ChipVersion = version;
                        return faster;
                    }

                    reader = await OpenAsync(portId, rate, cancellationToken);
                }

                ChipVersion = version;
                var found = reader;
                reader = null; // Ownership transfers to GetReaderAsync.
                return found;
            }
            finally
            {
                // Selection changes cancel probing. Dispose temporary streams before the next provider opens
                // the same COM port; these streams haven't been assigned to _reader yet.
                if (reader != null) await reader.Uart.DisposeAsync();
            }
        }

        throw new NfcReaderNotFoundException(
            $"No MFRC522 answered on {portId} (tried {string.Join(", ", rates)} baud). " +
            "Check the reader is plugged in and nothing else is using the port.", portId);
    }

    private async Task<Mfrc522Reader?> SwitchBaudRateAsync(Mfrc522Reader reader, string portId, byte speed, CancellationToken cancellationToken)
    {
        try
        {
            // The acknowledgement may come back at either rate, so it's not checked; the port is reopened instead.
            await reader.Uart.Port.WriteAsync([Mfrc522Reg.SerialSpeed, speed], cancellationToken);
            await Task.Delay(50, cancellationToken);
        }
        finally
        {
            await reader.Uart.DisposeAsync();
        }

        var faster = await OpenAsync(portId, BaudRate, cancellationToken);
        var retained = false;
        try
        {
            var version = await faster.GetVersionAsync(cancellationToken);
            if (version != null && Mfrc522Reg.KnownVersions.ContainsKey(version.Value))
            {
                retained = true;
                return faster;
            }

            // Put the chip back to the power-on rate so the fallback can reach it.
            try
            {
                await faster.Uart.Port.WriteAsync([Mfrc522Reg.SerialSpeed, Mfrc522Reg.SerialSpeeds[PowerOnBaudRate]], cancellationToken);
                await Task.Delay(50, cancellationToken);
            }
            catch
            {
            }
            return null;
        }
        finally
        {
            if (!retained) await faster.Uart.DisposeAsync();
        }
    }

    private async Task<Mfrc522Reader> OpenAsync(string portId, int rate, CancellationToken cancellationToken)
    {
        var port = await _ports.OpenAsync(portId, rate, cancellationToken);
        var uart = new Mfrc522Uart(port, rate);
        try
        {
            await uart.DrainAsync(TimeSpan.FromMilliseconds(30), cancellationToken);
            return new Mfrc522Reader(uart);
        }
        catch
        {
            await uart.DisposeAsync();
            throw;
        }
    }

    private async Task ResetAsync()
    {
        await _openLock.WaitAsync();
        try
        {
            await ResetCoreAsync();
        }
        finally
        {
            _openLock.Release();
        }
    }

    private async Task ResetCoreAsync()
    {
        if (_reader != null)
        {
            await RestorePowerOnStateAsync(_reader);
            try
            {
                await _reader.Uart.DisposeAsync();
            }
            catch
            {
            }
        }

        _reader = null;
        _openPortId = null;
    }

    /// <summary>
    /// Leaves the chip as it was found: RF field off and the UART back at its power-on 9600 baud, so other software (or
    /// the next session) can talk to it without knowing the faster rate. Best effort: the chip may already be gone.
    /// </summary>
    private static async Task RestorePowerOnStateAsync(Mfrc522Reader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await reader.AntennaOffAsync(timeout.Token);
        }
        catch
        {
        }

        if (reader.Uart.BaudRate == PowerOnBaudRate)
        {
            return;
        }

        try
        {
            // The acknowledgement may come back at either rate, so it isn't waited for.
            await reader.Uart.Port.WriteAsync([Mfrc522Reg.SerialSpeed, Mfrc522Reg.SerialSpeeds[PowerOnBaudRate]], timeout.Token);
            await Task.Delay(50, timeout.Token);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Closes the COM port (e.g. when another reader is selected). The next connect opens it again.
    /// </summary>
    public Task ReleaseAsync() => ResetAsync();

    public async ValueTask DisposeAsync() => await ResetAsync();
}

internal sealed class Mfrc522TagConnection : INfcTagConnection, INfcReselectable
{
    private readonly Mfrc522Reader _reader;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Mfrc522TagConnection(Mfrc522Reader reader, Mfrc522Card card)
    {
        _reader = reader;
        Card = card;
    }

    public Mfrc522Card Card
    {
        get;
    }

    public byte[] Uid => Card.Uid;

    // The FIFO is 64 bytes: 15 pages (60 bytes) plus the 2 byte CRC fits.
    public int MaxFastReadPages => 15;

    public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default) =>
        ExchangeAsync(command, null, false, cancellationToken);

    public Task<byte[]> TransceiveAsync(byte[] command, TimeSpan responseTimeout, CancellationToken cancellationToken = default) =>
        ExchangeAsync(command, responseTimeout, false, cancellationToken);

    public Task SendAsync(byte[] command, CancellationToken cancellationToken = default) =>
        ExchangeAsync(command, null, true, cancellationToken);

    private async Task<byte[]> ExchangeAsync(byte[] command, TimeSpan? responseTimeout, bool passiveAck = false, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        var previousTimeout = _reader.ExchangeTimeout;
        try
        {
            if (responseTimeout.HasValue)
            {
                _reader.ExchangeTimeout = responseTimeout.Value;
            }
            return await _reader.TransceiveAsync(command, passiveAck, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            throw new NfcException("Lost contact with the MFRC522.", ex);
        }
        finally
        {
            _reader.ExchangeTimeout = previousTimeout;
            _lock.Release();
        }
    }

    public async Task ReselectAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // WUPA wakes a tag that went idle or halted after a NAK. Only if that fails is the field cycled, which also
            // restarts a v3 figure's microcontroller.
            Mfrc522Card? card = null;
            for (var attempt = 0; attempt < 3 && card == null; attempt++)
            {
                card = await _reader.SelectAsync(cancellationToken);
            }

            if (card == null)
            {
                await _reader.ResetFieldAsync(cancellationToken);
                card = await _reader.SelectAsync(cancellationToken);
            }

            if (card == null)
            {
                throw new NfcException("The tag was moved away from the reader.");
            }

            if (!card.Uid.AsSpan().SequenceEqual(Card.Uid))
            {
                throw new NfcException("A different tag is on the reader now.");
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            throw new NfcException("Lost contact with the MFRC522.", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void SetStatus(string message)
    {
    }

    public async Task CloseAsync(string? errorMessage = null)
    {
        try
        {
            await _reader.HaltAsync();
        }
        catch
        {
            // The port stays open for the next tag; a failed HALT is harmless.
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
