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
    private readonly Queue<byte> _pendingBytes = new();
    private readonly HashSet<byte> _amiitoolCommands = [];

    /// <summary>Subcommands advertised by the firmware's EE capability query.</summary>
    public IReadOnlyCollection<byte> AmiitoolCommands => _amiitoolCommands.ToArray();
    public bool CanUploadKeys => AmiitoolEnabled && _amiitoolCommands.Contains(Cmd.AmiitoolKey);
    public bool CanRandomizeUid => AmiitoolEnabled && AmiitoolKeysLoaded && _amiitoolCommands.Contains(Cmd.AmiitoolChangeUid);

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

        _responses?.Dispose();
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
            var firmware = await ReadIdentityAsync(Cmd.GetFirmware, cancellationToken);
            Firmware = DecodeNullTerminated(firmware.AsSpan(firmware.Length > 0 && firmware[0] == Cmd.GetFirmware ? 1 : 0));
        }
        catch (TimeoutException)
        {
        }

        try
        {
            var name = await ReadIdentityAsync(Cmd.GetBluetoothName, cancellationToken);
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

    private async Task<byte[]> ReadIdentityAsync(byte command, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ClearInput();
            await SendChunkedAsync([command], null, cancellationToken);
            var bytes = new List<byte>();
            while (bytes.Count < 256)
            {
                var next = (await ReceiveBytesAsync(1, TimeSpan.FromSeconds(2), null, cancellationToken))[0];
                if (next == 0) return bytes.ToArray();
                bytes.Add(next);
            }
            throw new BleException("The puck returned an unterminated identity response.");
        }
        finally { _lock.Release(); }
    }

    private async Task SetupAmiitoolAsync(CancellationToken cancellationToken)
    {
        _amiitoolCommands.Clear();
        AmiitoolEnabled = AmiitoolKeysLoaded = false;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ClearInput();
            await SendChunkedAsync([Cmd.Amiitool], null, cancellationToken);
            var first = (await ReceiveBytesAsync(1, TimeSpan.FromSeconds(2), null, cancellationToken))[0];
            if (first == (byte)'B')
            {
                // Both linked UART builds use this text for unsupported commands.
                await ReceiveBytesAsync("Bad Command".Length - 1, TimeSpan.FromSeconds(2), null, cancellationToken);
                return;
            }
            if (first > Cmd.AmiitoolChangeUid) return;
            var advertised = new HashSet<byte> { first };
            while (advertised.Count < 3)
            {
                byte next;
                try { next = (await ReceiveBytesAsync(1, TimeSpan.FromMilliseconds(100), null, cancellationToken))[0]; }
                catch (TimeoutException) { break; } // A firmware may expose only a subset.
                if (next > Cmd.AmiitoolChangeUid || !advertised.Add(next)) return;
            }
            if (_pendingBytes.Count != 0) return; // Reject extra data, not a capability list.
            _amiitoolCommands.UnionWith(advertised);
            AmiitoolEnabled = true;
        }
        catch (TimeoutException) { return; }
        finally { _lock.Release(); }

        if (!_amiitoolCommands.Contains(Cmd.AmiitoolKey)) return;
        var keyCheck = await SendAndReceiveAsync([Cmd.Amiitool, Cmd.AmiitoolKey], 1, cancellationToken: cancellationToken);
        if (keyCheck[0] > 1) throw new BleException("Invalid amiitool key status response.");
        AmiitoolKeysLoaded = keyCheck[0] == 0;
        if (!AmiitoolKeysLoaded && _amiitoolKeys?.Invoke() is { Length: AmiiboKeySet.FileLength } keys)
            await UploadKeysAsync(keys, cancellationToken);
    }

    public async Task<bool> UploadKeysAsync(byte[] keys, CancellationToken cancellationToken = default)
    {
        if (!CanUploadKeys)
            throw new BleException("This puck doesn't support amiitool key uploads.");
        if (keys.Length != AmiiboKeySet.FileLength)
            throw new ArgumentException("Keys must be 160 bytes.", nameof(keys));
        await _lock.WaitAsync(cancellationToken);
        try
        {
            byte[] uploadCommand = [Cmd.Amiitool, Cmd.AmiitoolKey, 0x00];
            var ack = await SendAndReceiveLockedAsync(uploadCommand, 3, DefaultTimeout, null, null, cancellationToken);
            if (!ack.AsSpan().SequenceEqual(uploadCommand))
                throw new BleException("The puck didn't accept the key upload.");
            var result = await SendAndReceiveLockedAsync(keys, 1, DefaultTimeout, null, null, cancellationToken);
            if (result[0] > 1) throw new BleException("Invalid amiitool key upload response.");
            AmiitoolKeysLoaded = result[0] == 0;
            return AmiitoolKeysLoaded;
        }
        finally { _lock.Release(); }
    }

    public async Task<SlotSummary> GetSlotSummaryAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ClearInput();
            await SendChunkedAsync([Cmd.SlotInformation], null, cancellationToken);
            for (var skipped = 0; skipped < 4; skipped++)
            {
                var header = (await ReceiveBytesAsync(1, DefaultTimeout, null, cancellationToken))[0];
                if (header == Cmd.RestartNfc)
                {
                    // Older clients left this two-byte ACK pending; never parse it as a summary.
                    await ReceiveBytesAsync(1, DefaultTimeout, null, cancellationToken);
                    continue;
                }
                if (header != Cmd.SlotInformation) break;
                var summary = await ReceiveBytesAsync(2, DefaultTimeout, null, cancellationToken);
                if (summary[1] > 0 && summary[0] < summary[1]) return new SlotSummary(summary[0], summary[1]);
                break;
            }
            throw new BleException("Slot summary response invalid.");
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<SlotInfo>> GetSlotsAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var summary = await GetSlotSummaryAsync(cancellationToken);
        return await GetSlotInformationAsync(0, summary.Total, progress, cancellationToken);
    }

    public async Task<IReadOnlyList<SlotInfo>> GetSlotInformationAsync(int startSlot, int count, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (startSlot < 0 || startSlot > 255 || count < 0 || count > 255 || startSlot + count > 256)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return [];
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

    public async Task<SlotInfo> GetSlotInformationAsync(int slot, CancellationToken cancellationToken = default) =>
        (await GetSlotInformationAsync(slot, 1, cancellationToken: cancellationToken))[0];

    public async Task<TagDump> ReadSlotAsync(int slot, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        const int length = TagDump.FullLength + 4;
        var response = await SendAndReceiveAsync(
            [Cmd.Read, (byte)slot, 0, 143],
            length,
            receiveProgress: progress == null ? null : new Progress<int>(bytes => progress.Report(new TransferProgress(bytes, length, "Reading"))),
            cancellationToken: cancellationToken);

        if (!response.AsSpan(0, 4).SequenceEqual(new byte[] { Cmd.Read, (byte)slot, 0, 143 }))
            throw new BleException("Invalid slot read response.");
        return new TagDump(response.AsSpan(4, TagDump.FullLength));
    }

    public override async Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var data = dump.ToArray(TagDump.FullLength);
        var targetSlot = slot ?? (await GetSlotSummaryAsync(cancellationToken)).Current;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            byte[] start = [Cmd.FullWrite, (byte)targetSlot];
            var ack = await SendAndReceiveLockedAsync(start, 2, DefaultTimeout, null, null, cancellationToken);
            if (!ack.AsSpan().SequenceEqual(start)) throw new BleException("The puck didn't accept the slot write.");
            var complete = await SendAndReceiveLockedAsync(data, 6, DefaultTimeout,
                progress == null ? null : new Progress<int>(bytes => progress.Report(new TransferProgress(bytes, data.Length, "Writing"))),
                null, cancellationToken);
            if (complete[0] != Cmd.FullWrite || complete[1] != targetSlot)
                throw new BleException("Invalid slot write response.");
            progress?.Report(new TransferProgress(data.Length, data.Length, "Saving"));
            byte[] save = [Cmd.Save, (byte)targetSlot];
            var saved = await SendAndReceiveLockedAsync(save, 2, DefaultTimeout, null, null, cancellationToken);
            if (!saved.AsSpan().SequenceEqual(save)) throw new BleException("Invalid slot save response.");
            await SelectSlotLockedAsync(targetSlot, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    public async Task SelectSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try { await SelectSlotLockedAsync(slot, cancellationToken); }
        finally { _lock.Release(); }
    }

    private async Task SelectSlotLockedAsync(int slot, CancellationToken cancellationToken)
    {
        byte[] command = [Cmd.RestartNfc, (byte)slot];
        var ack = await SendAndReceiveLockedAsync(command, 2, DefaultTimeout, null, null, cancellationToken);
        if (!ack.AsSpan().SequenceEqual(command)) throw new BleException("Invalid slot selection response.");
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
        await SendAndReceiveAsync([Cmd.MoveSlot, (byte)from, (byte)to], 3, cancellationToken: cancellationToken);

    public async Task<byte[]> RandomizeUidAsync(int slot, CancellationToken cancellationToken = default)
    {
        if (!AmiitoolEnabled || !_amiitoolCommands.Contains(Cmd.AmiitoolChangeUid))
            throw new BleException("This puck doesn't support amiitool UID changes.");

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

        // The puck restarts its Bluetooth to advertise the new name; nothing may reconnect to it while it does.
        // TODO: On Windows the link still isn't dropped after a rename (puck only; Pixl.js disconnects fine), so the
        // puck never applies the name. Something in the app holds it: closing the app ends the session, and turning
        // Bluetooth off and on makes the app reconnect. Not fixed by switching off MaintainConnection on Plugin.BLE's
        // GATT session, nor by disposing every BluetoothLEDevice for it (PluginBleAdapter). See TODO.md.
        await Connection.ExpectDeviceDisconnectAsync();
        try
        {
            await SendAndReceiveAsync([Cmd.SetBluetoothName, .. bytes], bytes.Length + 1, timeout: RenameReplyTimeout, cancellationToken: cancellationToken);
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
        await Connection.ExpectDeviceDisconnectAsync();
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
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await SendAndReceiveLockedAsync(bytes, count, timeout ?? DefaultTimeout, sendProgress, receiveProgress, cancellationToken);
        }
        finally { _lock.Release(); }
    }

    private void ClearInput()
    {
        _pendingBytes.Clear();
        (_responses ?? throw new BleException("The puck isn't connected.")).Clear();
    }

    private async Task<byte[]> SendAndReceiveLockedAsync(byte[] bytes, int count, TimeSpan timeout,
        IProgress<int>? sendProgress, IProgress<int>? receiveProgress, CancellationToken cancellationToken)
    {
        ClearInput();
        await SendChunkedAsync(bytes, sendProgress, cancellationToken);
        return count > 0 ? await ReceiveBytesAsync(count, timeout, receiveProgress, cancellationToken)
            : await _responses!.ReceiveAsync(timeout, cancellationToken);
    }

    // Nordic UART is a byte stream: a firmware Bluetooth.write() may be fragmented or
    // coalesced with another one. Keep surplus bytes instead of discarding packet tails.
    private async Task<byte[]> ReceiveBytesAsync(int count, TimeSpan timeout, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            if (_pendingBytes.Count == 0)
            {
                var packet = await _responses!.ReceiveAsync(timeout, cancellationToken);
                foreach (var value in packet) _pendingBytes.Enqueue(value);
            }
            while (offset < count && _pendingBytes.TryDequeue(out var value)) result[offset++] = value;
            progress?.Report(offset);
        }
        return result;
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
