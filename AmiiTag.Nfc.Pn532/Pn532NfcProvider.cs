namespace AmiiTag.Nfc.Pn532;

/// <summary>PN532 in HSU/UART mode, at its power-on 115200 baud, through the shared serial adapters.</summary>
public sealed class Pn532NfcProvider : INfcProvider, INfcReaderRequester, INfcReleasable, IAsyncDisposable
{
    public const int BaudRate = 115200;
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
                System.Diagnostics.Debug.WriteLine($"PN532: opening {portId} at {BaudRate} baud (8N1).");
                _uart = new Pn532Uart(await _ports.OpenAsync(portId, BaudRate, cancellationToken));
                _portId = portId;
                await _uart.WakeAsync(cancellationToken);
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
                throw new NfcConnectionLostException($"PN532 on {portId} at {BaudRate} baud: {ex.Message} Check HSU / UART mode and the serial connection.", ex);
            throw;
        }
        finally { _gate.Release(); }
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
            await ResetCoreAsync();
        }
        finally { _gate.Release(); }
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
