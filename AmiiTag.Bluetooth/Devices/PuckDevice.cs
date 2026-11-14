using System.Text;
using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// A puck running the DTM puck firmware over the Nordic UART service. Port of the Swift <c>PuckPeripheral</c>.
/// </summary>
public sealed class PuckDevice : TagDeviceBase, IAmiitoolTagDevice, IRenamableTagDevice, IUartTagDevice
{
    private static class Cmd
    {
        public const byte SlotInformation = 0x01;
        public const byte Read = 0x02;
        public const byte Write = 0x03;
        public const byte Save = 0x04;
        public const byte FullWrite = 0x05;
        public const byte FullRead = 0x06;
        public const byte Amiitool = 0xEE;
        public const byte ClearSlot = 0xF9;
        public const byte GetBluetoothName = 0xFA;
        public const byte SetBluetoothName = 0xFB;
        public const byte GetFirmware = 0xFC;
        public const byte MoveSlot = 0xFD;
        public const byte EnableBleUart = 0xFE;
        public const byte RestartNfc = 0xFF;

        public const byte AmiitoolKey = 0x00;
        public const byte AmiitoolGenerateTag = 0x01;
        public const byte AmiitoolChangeUid = 0x02;
    }

    private const string FastModeMarker = "DTM_PUCK_FAST";
    private const int SlotInfoLength = 82;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<byte[]?>? _amiitoolKeys;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IBleCharacteristic? _command;
    private BleNotificationQueue? _responses;
    private int _packetSize = 20;
    private string? _name;

    /// <param name="connection">The BLE connection.</param>
    /// <param name="amiitoolKeys">Returns key_retail.bin (160 bytes) to upload to pucks with amiitool support, if available.</param>
    public PuckDevice(IBleConnection connection, Func<byte[]?>? amiitoolKeys = null) : base(connection)
    {
        _amiitoolKeys = amiitoolKeys;
    }

    public override TagDeviceKind Kind => TagDeviceKind.Puck;

    public override string Name => _name ?? base.Name;

    /// <summary>
    /// How long to wait for the fast mode marker during detection.
    /// </summary>
    public TimeSpan FastModeTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Sends Ctrl-C before <c>fastMode()</c>, clearing anything other probes left on the console's input line.
    /// </summary>
    public bool ClearInputLine { get; init; }

    public bool AmiitoolEnabled { get; private set; }

    public bool AmiitoolKeysLoaded { get; private set; }

    public int MaxNameBytes => 20;

    public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _command = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartWrite, cancellationToken);
        var notify = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartNotify, cancellationToken);
        if (_command == null || notify == null)
        {
            return false;
        }

        await Connection.RequestMtuAsync(247, cancellationToken);

        _responses = new BleNotificationQueue(notify);
        _responses.PacketReceived += (_, packet) =>
        {
            // Mirror the Swift app: the largest packet we've received tells us what the link can carry.
            if (packet.Length > _packetSize)
            {
                _packetSize = packet.Length;
            }
        };
        await notify.StartNotificationsAsync(cancellationToken);

        try
        {
            _responses.Clear();
            await SendChunkedAsync(Encoding.ASCII.GetBytes(ClearInputLine ? "\u0003fastMode()\n" : "fastMode()\n"), null, cancellationToken);
            await _responses.WaitForTextAsync(FastModeMarker, FastModeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }

        // Give the console a moment to finish echoing before switching to binary commands.
        await Task.Delay(100, cancellationToken);
        _responses.Clear();

        await TryReadIdentityAsync(cancellationToken);
        await SetupAmiitoolAsync(cancellationToken);
        return true;
    }

    private async Task TryReadIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var firmware = await SendAndReceiveAsync([Cmd.GetFirmware], timeout: TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);
            Firmware = DecodeNullTerminated(firmware.AsSpan(firmware.Length > 0 && firmware[0] == Cmd.GetFirmware ? 1 : 0));
        }
        catch (TimeoutException)
        {
        }

        try
        {
            var name = await SendAndReceiveAsync([Cmd.GetBluetoothName], timeout: TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);
            var decoded = DecodeNullTerminated(name.AsSpan(name.Length > 0 && name[0] == Cmd.GetBluetoothName ? 1 : 0));
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                _name = decoded;
            }
        }
        catch (TimeoutException)
        {
        }
    }

    private async Task SetupAmiitoolAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAndReceiveAsync([Cmd.Amiitool], timeout: TimeSpan.FromSeconds(2), cancellationToken: cancellationToken);
            AmiitoolEnabled = result.Length > 1;
        }
        catch (TimeoutException)
        {
            AmiitoolEnabled = false;
        }

        if (!AmiitoolEnabled)
        {
            return;
        }

        var keyCheck = await SendAndReceiveAsync([Cmd.Amiitool, Cmd.AmiitoolKey], cancellationToken: cancellationToken);
        AmiitoolKeysLoaded = keyCheck.Length == 1 && keyCheck[0] == 0x00;

        if (!AmiitoolKeysLoaded && _amiitoolKeys?.Invoke() is { Length: AmiiboKeySet.FileLength } keys)
        {
            await UploadKeysAsync(keys, cancellationToken);
        }
    }

    public async Task<bool> UploadKeysAsync(byte[] keys, CancellationToken cancellationToken = default)
    {
        if (keys.Length != AmiiboKeySet.FileLength)
        {
            throw new ArgumentException("Keys must be 160 bytes.", nameof(keys));
        }

        byte[] uploadCommand = [Cmd.Amiitool, Cmd.AmiitoolKey, 0x00];
        var ack = await SendAndReceiveAsync(uploadCommand, cancellationToken: cancellationToken);
        if (!ack.AsSpan().SequenceEqual(uploadCommand))
        {
            throw new BleException("The puck didn't accept the key upload.");
        }

        var result = await SendAndReceiveAsync(keys, cancellationToken: cancellationToken);
        AmiitoolKeysLoaded = result.Length == 1 && result[0] == 0x00;
        return AmiitoolKeysLoaded;
    }

    public async Task<SlotSummary> GetSlotSummaryAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAndReceiveAsync([Cmd.SlotInformation], cancellationToken: cancellationToken);
        if (response.Length < 3 || response[0] != Cmd.SlotInformation)
        {
            throw new BleException("Slot summary response invalid.");
        }

        return new SlotSummary(response[1], response[2]);
    }

    public async Task<IReadOnlyList<SlotInfo>> GetSlotsAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var summary = await GetSlotSummaryAsync(cancellationToken);
        return await GetSlotInformationAsync(0, summary.Total, progress, cancellationToken);
    }

    public async Task<IReadOnlyList<SlotInfo>> GetSlotInformationAsync(int startSlot, int count, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var total = SlotInfoLength * count;
        var received = await SendAndReceiveAsync(
            [Cmd.SlotInformation, (byte)startSlot, (byte)count],
            total,
            receiveProgress: progress == null ? null : new Progress<int>(bytes => progress.Report(new TransferProgress(bytes / SlotInfoLength, count, "Reading slots"))),
            cancellationToken: cancellationToken);

        var slots = new List<SlotInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var chunk = received.AsSpan(i * SlotInfoLength, SlotInfoLength);
            var slot = startSlot + i;
            if (chunk[0] != Cmd.SlotInformation || chunk[1] != slot)
            {
                throw new BleException($"Failed to read slot {slot}.");
            }

            slots.Add(new SlotInfo(slot, DumpFromSlotInfo(chunk[2..])));
        }

        return slots;
    }

    public async Task<TagDump> ReadSlotAsync(int slot, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        const int length = TagDump.FullLength + 4;
        var response = await SendAndReceiveAsync(
            [Cmd.Read, (byte)slot, 0, 143],
            length,
            receiveProgress: progress == null ? null : new Progress<int>(bytes => progress.Report(new TransferProgress(bytes, length, "Reading"))),
            cancellationToken: cancellationToken);

        return new TagDump(response.AsSpan(4, TagDump.FullLength));
    }

    public override async Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var data = dump.ToArray(TagDump.FullLength);
        var targetSlot = slot ?? (await GetSlotSummaryAsync(cancellationToken)).Current;

        await SendAndReceiveAsync([Cmd.FullWrite, (byte)targetSlot], cancellationToken: cancellationToken);
        await SendAndReceiveAsync(
            data,
            sendProgress: progress == null ? null : new Progress<int>(bytes => progress.Report(new TransferProgress(bytes, data.Length, "Writing"))),
            cancellationToken: cancellationToken);

        progress?.Report(new TransferProgress(data.Length, data.Length, "Saving"));
        await SendAndReceiveAsync([Cmd.Save, (byte)targetSlot], cancellationToken: cancellationToken);

        // Reload the slot so the new tag is emulated immediately.
        await SelectSlotAsync(targetSlot, cancellationToken);
    }

    public async Task SelectSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await RequireCommand().WriteAsync([Cmd.RestartNfc, (byte)slot], true, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<byte[]> ClearSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        var result = await SendAndReceiveAsync([Cmd.ClearSlot, (byte)slot], 11, cancellationToken: cancellationToken);
        if (result[0] != Cmd.ClearSlot)
        {
            throw new BleException("Invalid clear response.");
        }

        return result.AsSpan(2, 9).ToArray();
    }

    public async Task MoveSlotAsync(int from, int to, CancellationToken cancellationToken = default) =>
        await SendAndReceiveAsync([Cmd.MoveSlot, (byte)from, (byte)to], cancellationToken: cancellationToken);

    public async Task<byte[]> RandomizeUidAsync(int slot, CancellationToken cancellationToken = default)
    {
        if (!AmiitoolEnabled)
        {
            throw new BleException("This puck doesn't support amiitool.");
        }

        if (!AmiitoolKeysLoaded)
        {
            throw new BleException("The amiitool keys are not loaded on this puck.");
        }

        var result = await SendAndReceiveAsync([Cmd.Amiitool, Cmd.AmiitoolChangeUid, (byte)slot], 12, cancellationToken: cancellationToken);
        if (result[0] != Cmd.Amiitool || result[1] != Cmd.AmiitoolChangeUid || result[2] != slot)
        {
            throw new BleException("Invalid response when randomizing the UID.");
        }

        return result.AsSpan(3, 9).ToArray();
    }

    public async Task RenameAsync(string name, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > MaxNameBytes)
        {
            throw new BleException($"Name is too long ({bytes.Length} bytes, max {MaxNameBytes}).");
        }

        _name = name;
        try
        {
            await SendAndReceiveAsync([Cmd.SetBluetoothName, .. bytes], timeout: RenameReplyTimeout, cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            // The puck applies the name and restarts its Bluetooth, often before answering (or confirming the write).
        }

        await DisconnectAsync();
    }

    private static readonly TimeSpan RenameReplyTimeout = TimeSpan.FromSeconds(2);

    public async Task EnableUartAsync(CancellationToken cancellationToken = default)
    {
        await RequireCommand().WriteAsync([Cmd.EnableBleUart], true, cancellationToken);
        await DisconnectAsync();
    }

    public override void Detach()
    {
        _responses?.Dispose();
        _responses = null;
        base.Detach();
    }

    public override async Task DisconnectAsync()
    {
        _responses?.Dispose();
        _responses = null;
        await base.DisconnectAsync();
    }

    /// <summary>
    /// Sends bytes (split into packets) and waits for a reply.
    /// </summary>
    /// <param name="bytes">The data to send.</param>
    /// <param name="count">Bytes to collect from the reply, or 0 for "the next packet".</param>
    public async Task<byte[]> SendAndReceiveAsync(
        byte[] bytes,
        int count = 0,
        TimeSpan? timeout = null,
        IProgress<int>? sendProgress = null,
        IProgress<int>? receiveProgress = null,
        CancellationToken cancellationToken = default)
    {
        var responses = _responses ?? throw new BleException("The puck isn't connected.");
        await _lock.WaitAsync(cancellationToken);
        try
        {
            responses.Clear();
            await SendChunkedAsync(bytes, sendProgress, cancellationToken);
            var wait = timeout ?? DefaultTimeout;
            return count > 0
                ? await responses.ReceiveBytesAsync(count, wait, receiveProgress, cancellationToken)
                : await responses.ReceiveAsync(wait, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SendChunkedAsync(byte[] bytes, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var command = RequireCommand();
        for (var offset = 0; offset < bytes.Length; offset += _packetSize)
        {
            var length = Math.Min(_packetSize, bytes.Length - offset);
            await command.WriteAsync(bytes.AsSpan(offset, length).ToArray(), true, cancellationToken);
            progress?.Report(offset + length);
        }
    }

    private IBleCharacteristic RequireCommand() => _command ?? throw new BleException("The puck isn't connected.");

    private static string DecodeNullTerminated(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes).Trim();
    }
}
