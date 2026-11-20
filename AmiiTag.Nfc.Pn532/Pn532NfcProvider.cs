namespace AmiiTag.Nfc.Pn532;

/// <summary>
/// PN532 in HSU/UART mode through the shared serial adapters: found at its power-on 115200 baud, then switched to
/// <see cref="BaudRate"/> (SetSerialBaudRate, UM0701-02 7.2.13) when that's faster. The PN532 keeps a switched rate until
/// it loses power, so it's put back to 115200 on release, and a later connection also tries the switched rate.
/// </summary>
public sealed class Pn532NfcProvider : INfcProvider, INfcReaderRequester, INfcReleasable, INfcSerialProbe, INfcBaudRateSetting, IAsyncDisposable
{
    public const int PowerOnBaudRate = 115200;
    public const int FastBaudRate = 921600;

    /// <summary>
    /// The speeds offered in Settings (the same as the MFRC522's). 1,288,000 is left out: few USB serial adapters can
    /// produce it.
    /// </summary>
    public static readonly IReadOnlyList<int> SettingsBaudRates = [115200, 230400, 460800, 921600];

    // SetSerialBaudRate's BR codes.
    private static readonly Dictionary<int, byte> SerialSpeeds = new()
    {
        [9600] = 0x00, [19200] = 0x01, [38400] = 0x02, [57600] = 0x03, [115200] = 0x04,
        [230400] = 0x05, [460800] = 0x06, [921600] = 0x07, [1288000] = 0x08,
    };

    // The speed asked for (stays at the power-on rate until set; the app applies the Settings speed), and the speed the
    // PN532 is at as far as this provider knows.
    private int _targetBaudRate = PowerOnBaudRate;
    private int _linkBaudRate = PowerOnBaudRate;

    // The port the PN532 was last opened on: still known after an error or cancellation closed it, so release can
    // reopen it to put a switched PN532 back to its power-on rate.
    private string? _lastPortId;

    /// <summary>
    /// The speed to switch the PN532 to once it's found.
    /// </summary>
    public int BaudRate
    {
        get => _targetBaudRate;
        set => _targetBaudRate = SerialSpeeds.ContainsKey(value) ? value : PowerOnBaudRate;
    }

    int INfcBaudRateSetting.DefaultBaudRate => FastBaudRate;

    public IReadOnlyList<int> SupportedBaudRates => SettingsBaudRates;

    /// <summary>
    /// The speed the serial link runs at now.
    /// </summary>
    public int LinkBaudRate => _linkBaudRate;

    /// <summary>
    /// How long the auto-detection probe (<see cref="ProbeAsync"/>) waits for SAMConfiguration's ACK and response.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(100);
    private readonly ISerialPortFactory _ports;
    // One gate covers lifecycle and complete RF exchanges (including temporary register/timing settings).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Pn532Uart? _uart;
    private string? _portId;
    private int _session;
    // Retain the last UID even when a cancelled background poll closes/reopens the USB port. HALT is RF state
    // in the tag; closing a UART handle does not wake it. Scope the remembered UID to its serial port.
    private byte[]? _lastUid;
    private string? _lastUidPortId;

    public Pn532NfcProvider(ISerialPortFactory ports) => _ports = ports;
    public string Id => "pn532-serial";
    public string DisplayName => "PN532 (UART / serial / COM port)";
    public bool IsSupported => _ports.IsSupported;
    public bool ShowsSystemPrompt => false;
    public bool SupportsReaderSelection => true;
    public bool CanRequestReader => _ports is ISerialPortRequester;
    public byte[]? FirmwareVersion { get; private set; }

    public Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default) =>
        _ports.GetPortsAsync(cancellationToken);

    public Task<NfcReaderInfo?> RequestReaderAsync(CancellationToken cancellationToken = default) =>
        _ports is ISerialPortRequester requester
            ? requester.RequestPortAsync(cancellationToken)
            : Task.FromResult<NfcReaderInfo?>(null);

    public async Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default)
    {
        var ports = await GetReadersAsync(cancellationToken);
        var portId = ports.FirstOrDefault(p => p.Id == options.ReaderId)?.Id ?? ports.FirstOrDefault()?.Id;
        if (portId == null && options.AllowUserPrompts && CanRequestReader)
            portId = (await RequestReaderAsync(cancellationToken))?.Id ?? throw new OperationCanceledException();
        if (portId == null) throw new NfcException("No serial ports were found. Plug in the PN532 reader and try again.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ++_session;
            if (_uart == null || _uart.IsClosed || _portId != portId)
            {
                await ResetCoreAsync();
                await OpenCoreAsync(portId, cancellationToken);
            }

            // Also when the port was left open by ProbeAsync (at the power-on rate), or the speed was changed since.
            if (_linkBaudRate != _targetBaudRate)
            {
                await SwitchBaudRateAsync(portId, _targetBaudRate, cancellationToken);
            }

            // Reapply settings on each connection: a power blip can reset a PN532 while its USB adapter stays open.
            // SAM normal mode is required to leave the PN532's LowVbat state.
            await CommandAsync(0x14, [0x01, 0x14, 0x00], cancellationToken);
            var firmware = await CommandAsync(0x02, [], cancellationToken);
            if (firmware.Length != 4 || firmware[0] != 0x32 || (firmware[3] & 1) == 0)
                throw new NfcException("No ISO14443-A PN532 answered. Check the selected reader type and HSU / UART mode.");
            FirmwareVersion = firmware;
            System.Diagnostics.Debug.WriteLine($"PN532: {portId}, firmware {firmware[1]}.{firmware[2]}, support 0x{firmware[3]:X2}.");
            await CommandAsync(0x12, [0x10], cancellationToken); // No automatic ISO14443-4 RATS.
            await CommandAsync(0x32, [0x05, 0xFF, 0x01, 0x00], cancellationToken); // One passive activation attempt.
            await CommandAsync(0x32, [0x04, 0x00], cancellationToken); // Never replay a write on RF error.
            await SetRfTimeoutAsync(0x0B, cancellationToken);
            await ConfigureRadioAsync(raw: false, cancellationToken);

            while (true)
            {
                byte[] target;
                if (_lastUidPortId == portId && _lastUid != null)
                {
                    // UM0701-02 7.3.5: supplying InitiatorData makes PN532 use ALL_REQ (WUPA), whereas an empty
                    // InitiatorData uses SENS_REQ (REQA). A tag halted by CloseTagAsync only answers WUPA.
                    target = await CommandAsync(0x4A, [0x01, 0x00, .. SelectionUid(_lastUid)], cancellationToken);
                    if (target.Length == 1 && target[0] == 0)
                        target = await CommandAsync(0x4A, [0x01, 0x00], cancellationToken); // A different tag may be present.
                }
                else
                {
                    target = await CommandAsync(0x4A, [0x01, 0x00], cancellationToken);
                }
                if (target.Length == 0) throw new NfcException("Truncated PN532 target response.");
                if (target[0] != 0)
                {
                    // NbTg, Tg, SENS_RES[2], SEL_RES, NFCIDLength, NFCID...
                    if (target[0] != 1 || target.Length < 6 || target[5] is not (4 or 7 or 10) || target.Length < 6 + target[5])
                        throw new NfcException("Invalid PN532 ISO14443-A target response.");
                    await ConfigureRawAsync(cancellationToken);
                    var uid = target[6..(6 + target[5])];
                    _lastUid = uid.ToArray();
                    _lastUidPortId = portId;
                    return new Pn532TagConnection(this, _session, target[1], uid);
                }
                await Task.Delay(150, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            await ResetCoreAsync();
            if (ex is TimeoutException or IOException)
                throw new NfcConnectionLostException($"PN532 on {portId} at {_linkBaudRate} baud: {ex.Message} Check HSU / UART mode and the serial connection.", ex);
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Whether a PN532 is on the port: opens it at <see cref="PowerOnBaudRate"/> (then a rate it may have been left at) and sends the wake preamble with
    /// SAMConfiguration (the first command of a connection). An ACK and response within <see cref="ProbeTimeout"/>
    /// means a PN532, and the port stays open for ConnectAsync; anything else closes it again.
    /// </summary>
    public async Task<bool> ProbeAsync(string portId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ResetCoreAsync();

            // The power-on rate first; then a switched rate a PN532 may still be at (it keeps one until power off).
            foreach (var rate in new[] { PowerOnBaudRate, _linkBaudRate, _targetBaudRate }.Distinct())
            {
                System.Diagnostics.Debug.WriteLine($"PN532: probing {portId} at {rate} baud.");
                var uart = new Pn532Uart(await _ports.OpenAsync(portId, rate, cancellationToken));
                try
                {
                    await uart.CommandAsync(0x14, [0x01, 0x14, 0x00], ProbeTimeout, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is TimeoutException or NfcException or IOException)
                {
                    // CommandAsync has closed the port.
                    System.Diagnostics.Debug.WriteLine($"PN532: no answer on {portId} at {rate} baud: {ex.Message}");
                    await uart.DisposeAsync();
                    continue;
                }

                System.Diagnostics.Debug.WriteLine($"PN532: answered on {portId} at {rate} baud.");
                _uart = uart;
                _portId = portId;
                _lastPortId = portId;
                _linkBaudRate = rate;
                return true;
            }

            return false;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Opens the port and wakes the PN532. At the power-on rate (the usual case) that's all; when it may have been left
    /// at a switched rate, each candidate rate is tried with SAMConfiguration until one answers.
    /// </summary>
    private async Task OpenCoreAsync(string portId, CancellationToken cancellationToken)
    {
        var rates = new[] { _linkBaudRate, PowerOnBaudRate, _targetBaudRate }.Distinct().ToList();
        if (rates.Count == 1)
        {
            System.Diagnostics.Debug.WriteLine($"PN532: opening {portId} at {_linkBaudRate} baud (8N1).");
            _uart = new Pn532Uart(await _ports.OpenAsync(portId, _linkBaudRate, cancellationToken));
            _portId = portId;
            _lastPortId = portId;
            await _uart.WakeAsync(cancellationToken);
            return;
        }

        Exception? failure = null;
        foreach (var rate in rates)
        {
            System.Diagnostics.Debug.WriteLine($"PN532: opening {portId} at {rate} baud (8N1).");
            var uart = new Pn532Uart(await _ports.OpenAsync(portId, rate, cancellationToken));
            try
            {
                await uart.WakeAsync(cancellationToken);
                await uart.CommandAsync(0x14, [0x01, 0x14, 0x00], TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is TimeoutException or NfcException or IOException)
            {
                failure = ex;
                await uart.DisposeAsync();
                continue;
            }

            _uart = uart;
            _portId = portId;
            _lastPortId = portId;
            _linkBaudRate = rate;
            return;
        }

        throw failure!;
    }

    /// <summary>
    /// SetSerialBaudRate: the PN532 acknowledges and answers at the current rate, the host confirms with an ACK, and both
    /// sides change. A PN532 that refuses stays at the current rate; one that switched but can't be heard at the new
    /// rate (an adapter that can't do it) is stuck there until it loses power, which the error says.
    /// </summary>
    private async Task SwitchBaudRateAsync(string portId, int rate, CancellationToken cancellationToken)
    {
        if (!SerialSpeeds.TryGetValue(rate, out var code))
        {
            return;
        }

        try
        {
            await _uart!.CommandAsync(0x10, [code], cancellationToken: cancellationToken);
            await _uart.SendAckAsync(cancellationToken);

            // Let the ACK leave the adapter before the port is closed; the PN532 changes rate after it.
            await Task.Delay(20, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is TimeoutException or NfcException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"PN532: couldn't switch to {rate} baud, staying at {_linkBaudRate}: {ex.Message}");
            await ResetCoreAsync();
            _targetBaudRate = _linkBaudRate;
            await OpenCoreAsync(portId, cancellationToken);
            return;
        }

        var previous = _linkBaudRate;
        await _uart.DisposeAsync();
        _uart = null;
        _linkBaudRate = rate;
        try
        {
            _uart = new Pn532Uart(await _ports.OpenAsync(portId, rate, cancellationToken));
            _portId = portId;
            _lastPortId = portId;
            await _uart.CommandAsync(0x02, [], TimeSpan.FromMilliseconds(500), cancellationToken);
            System.Diagnostics.Debug.WriteLine($"PN532: switched {portId} from {previous} to {rate} baud.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is TimeoutException or NfcException or IOException)
        {
            await ResetCoreAsync();
            throw new NfcConnectionLostException(
                $"The PN532 didn't answer after switching to {rate:N0} baud; the USB serial adapter may not support that speed. " +
                "Unplug the reader to reset it, and pick a lower speed.", ex);
        }
    }

    private static byte[] SelectionUid(byte[] uid) => uid.Length switch
    {
        // PN532's InitiatorData includes cascade tags, unlike the NFCID returned by target discovery.
        4 => uid.ToArray(),
        7 => [0x88, .. uid],
        10 => [0x88, .. uid[..3], 0x88, .. uid[3..]],
        _ => throw new NfcException("Invalid ISO14443-A UID length.")
    };

    private Task<byte[]> CommandAsync(byte command, byte[] data, CancellationToken token) =>
        _uart!.CommandAsync(command, data, cancellationToken: token);

    private Task<byte[]> ConfigureRawAsync(CancellationToken token) => ConfigureRadioAsync(raw: true, token);

    private Task<byte[]> ConfigureRadioAsync(bool raw, CancellationToken token) =>
        // Transmit CRC must remain enabled for both raw NFC commands and firmware-generated HLTA/SELECT.
        // Only receive CRC is disabled for raw exchanges, allowing 4-bit ACK/NAK replies. Full replies are
        // still checked/stripped by the host. Firmware selection/deselection uses normal receive CRC mode.
        CommandAsync(0x08, [0x63, 0x02, 0x80, 0x63, 0x03, raw ? (byte)0x00 : (byte)0x80, 0x63, 0x3D, 0x00], token);

    private Task<byte[]> SetRfTimeoutAsync(byte value, CancellationToken token) =>
        CommandAsync(0x32, [0x02, 0x00, 0x0B, value], token);

    private static byte EncodeTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > 3276.8)
            throw new ArgumentOutOfRangeException(nameof(timeout), "PN532 RF timeout must be between zero and 3276.8 ms.");
        byte value = 1;
        for (var milliseconds = 0.1; milliseconds < timeout.TotalMilliseconds && value < 16; milliseconds *= 2) ++value;
        return value;
    }

    internal async Task<byte[]> ExchangeAsync(int session, byte[] command, bool passiveAck, TimeSpan? timeout, CancellationToken token)
    {
        if (command.Length is < 1 or > 262) throw new ArgumentOutOfRangeException(nameof(command));
        var rfTimeout = timeout.HasValue ? EncodeTimeout(timeout.Value) : (byte)0x0B;
        await _gate.WaitAsync(token);
        try
        {
            EnsureSession(session);
            if (rfTimeout != 0x0B) await SetRfTimeoutAsync(rfTimeout, token);
            try
            {
                System.Diagnostics.Debug.WriteLine($"PN532 NFC TX: opcode 0x{command[0]:X2}, {command.Length} bytes.");
                var result = await _uart!.CommandAsync(0x42, command,
                    TimeSpan.FromSeconds(2) + (timeout ?? TimeSpan.Zero), token);
                if (result.Length == 0) throw new NfcException("Truncated PN532 RF response.");
                System.Diagnostics.Debug.WriteLine($"PN532 NFC RX: opcode 0x{command[0]:X2}, status 0x{result[0]:X2}, {result.Length - 1} RF bytes.");
                if (result[0] == 0x01 && passiveAck) return []; // Only the chip's RF timeout is a passive ACK.
                CheckStatus(result[0], command[0]);
                var data = result[1..];
                if (data.Length == 1)
                {
                    var control = await CommandAsync(0x06, [0x63, 0x3C], token);
                    if (control.Length != 1 || (control[0] & 7) != 4)
                        throw new NfcException("Invalid PN532 short RF response.");
                    var nibble = (byte)(data[0] & 0x0F);
                    if (nibble != 0x0A) throw new NfcNakException(nibble, $"The tag refused the command (NAK 0x{nibble:X}).");
                    return [nibble];
                }
                if (!Iso14443Crc.Check(data)) throw new NfcException($"The tag response to NFC command 0x{command[0]:X2} has an invalid CRC_A ({data.Length} RF bytes).");
                return data[..^2];
            }
            finally
            {
                if (rfTimeout != 0x0B && _uart is { IsClosed: false })
                {
                    // Complete restoration even if the caller cancelled after the RF exchange.
                    await SetRfTimeoutAsync(0x0B, CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
            throw new NfcConnectionLostException("Lost contact with the PN532.", ex);
        }
        finally { _gate.Release(); }
    }

    internal async Task ReselectAsync(int session, byte target, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            EnsureSession(session);
            // InSelect reactivates the stored UID; it cannot silently switch to a different tag. InDeselect sends
            // HLTA without switching off the RF field, preserving a v3 figure's microcontroller state.
            await ConfigureRadioAsync(raw: false, token);
            CheckStatusResponse(await CommandAsync(0x44, [target], token));
            CheckStatusResponse(await CommandAsync(0x54, [target], token));
            await ConfigureRawAsync(token);
        }
        finally { _gate.Release(); }
    }

    internal async Task CloseTagAsync(int session, byte target)
    {
        await _gate.WaitAsync();
        try
        {
            if (session != _session || _uart is not { IsClosed: false }) return;
            ++_session;
            try
            {
                await ConfigureRadioAsync(raw: false, CancellationToken.None);
                await CommandAsync(0x44, [target], CancellationToken.None);
            }
            catch { /* A removed tag must not prevent closing a session. */ }
        }
        finally { _gate.Release(); }
    }

    private void EnsureSession(int session)
    {
        if (session != _session || _uart is not { IsClosed: false })
            throw new NfcConnectionLostException("The PN532 tag session has ended. Connect again.", new IOException("Closed session."));
    }

    private static void CheckStatusResponse(byte[] response)
    {
        if (response.Length != 1) throw new NfcException("Invalid PN532 status response.");
        CheckStatus(response[0]);
    }

    private static void CheckStatus(byte status, byte? command = null)
    {
        if (status == 1) throw new NfcException("The tag did not answer the PN532.");
        if (status != 0)
        {
            var context = command.HasValue ? $" for NFC command 0x{command.Value:X2}" : string.Empty;
            var description = status == 0x0B ? " (RF protocol error)" : string.Empty;
            throw new NfcException($"PN532 RF error 0x{status:X2}{description}{context}.");
        }
    }

    private async Task ResetCoreAsync()
    {
        ++_session;
        if (_uart != null) await _uart.DisposeAsync();
        _uart = null;
        _portId = null;
        FirmwareVersion = null;
    }

    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_uart is { IsClosed: false })
            {
                try { await CommandAsync(0x32, [0x01, 0x00], CancellationToken.None); }
                catch { }
            }

            await RestorePowerOnRateAsync();
            await ResetCoreAsync();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Puts a PN532 left at a switched rate back to 115200, so whatever opens the port next finds it there. Reopens the
    /// port when an error or a cancelled command has closed it (the PN532 keeps its rate regardless). Best effort: the
    /// reader may already be gone.
    /// </summary>
    private async Task RestorePowerOnRateAsync()
    {
        if (_linkBaudRate == PowerOnBaudRate || _lastPortId is not { } portId || !SerialSpeeds.TryGetValue(PowerOnBaudRate, out var code))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var uart = _uart is { IsClosed: false } open ? open : null;
        try
        {
            if (uart == null)
            {
                uart = new Pn532Uart(await _ports.OpenAsync(portId, _linkBaudRate, timeout.Token));

                // Drains anything late from an interrupted exchange first.
                await uart.WakeAsync(timeout.Token);
            }

            await uart.CommandAsync(0x10, [code], TimeSpan.FromMilliseconds(500), timeout.Token);
            await uart.SendAckAsync(timeout.Token);
            await Task.Delay(20, timeout.Token);
            _linkBaudRate = PowerOnBaudRate;
            System.Diagnostics.Debug.WriteLine($"PN532: {portId} back at {PowerOnBaudRate} baud.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PN532: couldn't put {portId} back at {PowerOnBaudRate} baud: {ex.Message}");
        }
        finally
        {
            if (uart != null && !ReferenceEquals(uart, _uart))
            {
                await uart.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}

internal sealed class Pn532TagConnection(Pn532NfcProvider provider, int session, byte target, byte[] uid)
    : INfcTagConnection, INfcReselectable
{
    public byte[] Uid => uid;
    public int MaxFastReadPages => 63;
    public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default) =>
        provider.ExchangeAsync(session, command, false, null, cancellationToken);
    public Task<byte[]> TransceiveAsync(byte[] command, TimeSpan responseTimeout, CancellationToken cancellationToken = default) =>
        provider.ExchangeAsync(session, command, false, responseTimeout, cancellationToken);
    public async Task SendAsync(byte[] command, CancellationToken cancellationToken = default) =>
        await provider.ExchangeAsync(session, command, true, null, cancellationToken);
    public Task ReselectAsync(CancellationToken cancellationToken = default) => provider.ReselectAsync(session, target, cancellationToken);
    public void SetStatus(string message) { }
    public Task CloseAsync(string? errorMessage = null) => provider.CloseTagAsync(session, target);
    public async ValueTask DisposeAsync() => await CloseAsync();
}
