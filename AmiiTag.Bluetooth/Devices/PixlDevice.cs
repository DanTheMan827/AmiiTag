using System.Buffers.Binary;
using System.Text;
using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// A storage drive on a Pixl.js ("E" is the external flash).
/// </summary>
/// <param name="Label">The drive letter, used in paths ("E:/amiibo").</param>
/// <param name="Name">The drive's name.</param>
/// <param name="IsAvailable">False when the drive isn't mounted (it may need formatting).</param>
/// <param name="TotalBytes">Capacity.</param>
/// <param name="FreeBytes">Space left.</param>
public sealed record PixlDrive(char Label, string Name, bool IsAvailable, long TotalBytes, long FreeBytes)
{
    public string RootPath => $"{Label}:/";

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>
/// Extra information the Pixl.js keeps with a file.
/// </summary>
/// <param name="Notes">A note shown on the device (up to 90 bytes of UTF-8).</param>
/// <param name="Hidden">Hidden from the device's amiibo list (still shown in file managers).</param>
/// <param name="AmiiboId">The amiibo ID (head + tail, 16 hex digits) for files in the amiibo data folder.</param>
public sealed record PixlFileMeta(string? Notes = null, bool Hidden = false, string? AmiiboId = null)
{
    public const int MaxNotesBytes = 90;

    public static PixlFileMeta Empty { get; } = new();

    public bool IsEmpty => string.IsNullOrEmpty(Notes) && !Hidden && AmiiboId == null;
}

/// <summary>
/// A file or folder on a Pixl.js.
/// </summary>
public sealed record PixlEntry(string Name, string Path, long Size, bool IsFolder, PixlFileMeta Meta);

/// <summary>
/// A non-zero status in a Pixl.js reply.
/// </summary>
public sealed class PixlException : BleException
{
    public PixlException(byte command, byte status)
        : base(Describe(command, status))
    {
        Command = command;
        Status = status;
    }

    public byte Command { get; }

    public byte Status { get; }

    // 1 is the firmware's generic failure; reads report the file system's (negative) error code instead.
    private static string Describe(byte command, byte status) => status switch
    {
        0xFF => $"This Pixl.js firmware doesn't support command 0x{command:X2}. Update the firmware.",
        1 => command switch
        {
            PixlDevice.CmdOpenFile => "The Pixl.js couldn't open the file (it may not exist, or the folder may be missing).",
            PixlDevice.CmdWriteFile or PixlDevice.CmdCloseFile => "The Pixl.js couldn't save the file (the drive may be full).",
            PixlDevice.CmdCreateFolder => "The Pixl.js couldn't create the folder (it may already exist).",
            PixlDevice.CmdRename => "The Pixl.js couldn't rename it (the name may be taken, or it's on another drive).",
            PixlDevice.CmdFormatDrive => "The Pixl.js couldn't format the drive.",
            _ => $"The Pixl.js couldn't complete command 0x{command:X2} (it may not exist).",
        },
        _ => (sbyte)status switch
        {
            -2 => "The Pixl.js ran out of memory.",
            -4 => "That name already exists on the Pixl.js.",
            -5 => "That name is too long for the Pixl.js.",
            -6 => "The Pixl.js drive isn't available.",
            -7 => "The Pixl.js file system is corrupt. Formatting the drive may be needed.",
            -90 => "That file or folder doesn't exist on the Pixl.js.",
            -91 => "The Pixl.js drive is full.",
            var code => $"The Pixl.js reported error {code} for command 0x{command:X2}.",
        },
    };
}

/// <summary>
/// Pixl.js (solosky/pixl.js), an nRF52 amiibo emulator with an OLED screen and a flash file system, managed over its
/// BLE file transfer protocol (docs/en/05+1-ble_protocol.md; firmware fw/application/src/mod/df).
/// </summary>
/// <remarks>
/// <para>Nordic UART service; little endian; strings are a 16 bit length then UTF-8. The firmware handles every
/// frame on its own (there's no reassembly), so a request is always one frame; only replies (and the data of a file
/// write) span frames.</para>
/// <code>
/// frame   = cmd u8 | status u8 | chunk u16 | payload (up to 243 bytes)
/// status  = 0 ok, 1 failed, FF unknown command; file reads report the file system's negative error code
/// chunk   = sequence number (from 0) | 0x8000 while more frames of the same message follow
/// 01 get version          → str version, str ble address
/// 02 enter DFU            → (then restarts into the bootloader)
/// 10 drive list           → u8 count, { u8 status (0 = mounted), char label, str name, u32 total, u32 free }
/// 11 format drive         ← char label
/// 12 open file            ← str path, u32 flags (08 read only; 16 = 02 truncate | 04 create | 10 write only) → u8 0
/// 13 close file           ← u8 id
/// 14 read file            ← u8 id → the whole file, over as many frames as it takes
/// 15 write file           ← frames of u8 id + data, 0x8000 on all but the last → one reply after the last
/// 16 read folder          ← str path → { str name, u32 size, u8 type (1 = folder), u8 meta length, meta }
/// 17 create folder, 18 remove ← str path; 19 rename ← str old, str new (same drive)
/// 1A update meta          ← str path, u8 meta size, meta
/// meta    = { 01 u8 length notes | 02 u8 flags (01 hidden, 02 system, 04 read only) | 03 u32 head u32 tail }
/// </code>
/// Only one file can be open at a time (opening another closes it), so file operations are serialised here.
/// The device also offers an AmiiboLink compatible mode (it then advertises as "amiibolink" and is handled as one).
/// </remarks>
public sealed class PixlDevice : TagDeviceBase, IFileTagDevice
{
    public const byte CmdGetVersion = 0x01;
    public const byte CmdEnterDfu = 0x02;
    public const byte CmdDriveList = 0x10;
    public const byte CmdFormatDrive = 0x11;
    public const byte CmdOpenFile = 0x12;
    public const byte CmdCloseFile = 0x13;
    public const byte CmdReadFile = 0x14;
    public const byte CmdWriteFile = 0x15;
    public const byte CmdReadFolder = 0x16;
    public const byte CmdCreateFolder = 0x17;
    public const byte CmdRemove = 0x18;
    public const byte CmdRename = 0x19;
    public const byte CmdUpdateMeta = 0x1A;

    // vfs_mode_t flags.
    public const uint ModeTruncate = 0x02;
    public const uint ModeCreate = 0x04;
    public const uint ModeRead = 0x08;
    public const uint ModeWriteOnly = 0x10;
    public const uint ModeWrite = ModeTruncate | ModeCreate | ModeWriteOnly;

    public const ushort MoreChunks = 0x8000;
    public const int HeaderLength = 4;

    /// <summary>
    /// The protocol's largest frame payload (an MTU of 250, less ATT and frame headers).
    /// </summary>
    public const int MaxFramePayload = 243;

    public const int MaxPathBytes = 63;
    public const int MaxNameBytes = 47;

    public const byte MetaNotes = 0x01;
    public const byte MetaFlags = 0x02;
    public const byte MetaAmiibo = 0x03;
    public const byte FlagHidden = 0x01;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private IBleCharacteristic? _write;
    private BleNotificationQueue? _responses;
    private int _framePayload = MaxFramePayload;

    public PixlDevice(IBleConnection connection) : base(connection)
    {
    }

    public override TagDeviceKind Kind => TagDeviceKind.Pixl;

    /// <summary>
    /// The device's Bluetooth address, if the firmware reports it.
    /// </summary>
    public string? Address { get; private set; }

    /// <summary>
    /// Where amiibo uploaded from elsewhere in the app go.
    /// </summary>
    public string DefaultFolder => "E:/amiibo";

    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait for the version reply when probing an unknown device.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _write = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartWrite, cancellationToken);
        var notify = await TryGetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartNotify, cancellationToken);
        if (_write == null || notify == null)
        {
            return false;
        }

        // The firmware is built for an MTU of 250. Where the platform reports a smaller one, keep frames inside it.
        var mtu = await Connection.RequestMtuAsync(250, cancellationToken);
        _framePayload = mtu > 0 ? Math.Clamp(mtu - 3 - HeaderLength, 16, MaxFramePayload) : MaxFramePayload;

        _responses = new BleNotificationQueue(notify);
        await notify.StartNotificationsAsync(cancellationToken);

        try
        {
            var reply = await RequestAsync(CmdGetVersion, [], ProbeTimeout, null, cancellationToken);
            var reader = new Reader(reply);
            Firmware = reader.String();
            if (reader.Remaining >= 2)
            {
                try
                {
                    Address = reader.String();
                }
                catch (BleException)
                {
                    // Older firmware only sends the version.
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or BleException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- Drives and folders

    public async Task<IReadOnlyList<PixlDrive>> GetDrivesAsync(CancellationToken cancellationToken = default)
    {
        var reader = new Reader(await RequestAsync(CmdDriveList, [], null, null, cancellationToken));
        var count = reader.Byte();
        var drives = new List<PixlDrive>(count);
        for (var i = 0; i < count; i++)
        {
            var status = reader.Byte();
            var label = (char)reader.Byte();
            var name = reader.String();
            var total = reader.UInt32();
            var free = reader.UInt32();
            drives.Add(new PixlDrive(label, name, status == 0, total, free));
        }

        return drives;
    }

    /// <summary>
    /// Erases a drive. Everything on it is lost.
    /// </summary>
    public Task FormatDriveAsync(char label, CancellationToken cancellationToken = default) =>
        RequestAsync(CmdFormatDrive, [(byte)char.ToUpperInvariant(label)], TimeSpan.FromSeconds(60), null, cancellationToken);

    /// <summary>
    /// Lists a folder (folders first, then files, by name).
    /// </summary>
    public async Task<IReadOnlyList<PixlEntry>> ReadFolderAsync(string path, CancellationToken cancellationToken = default)
    {
        var reader = new Reader(await RequestAsync(CmdReadFolder, EncodeString(path, MaxPathBytes), TimeSpan.FromSeconds(10), null, cancellationToken));
        var entries = new List<PixlEntry>();
        while (reader.Remaining > 0)
        {
            var name = reader.String();
            var size = reader.UInt32();
            var type = reader.Byte();
            var metaLength = reader.Byte();
            var meta = reader.Bytes(metaLength);
            entries.Add(new PixlEntry(name, PixlPath.Combine(path, name), size, type == 1, ParseMeta(meta)));
        }

        return entries
            .OrderByDescending(e => e.IsFolder)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task CreateFolderAsync(string path, CancellationToken cancellationToken = default) =>
        RequestAsync(CmdCreateFolder, EncodeString(path, MaxPathBytes), null, null, cancellationToken);

    /// <summary>
    /// Deletes a file, or a folder with everything in it.
    /// </summary>
    public Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        RequestAsync(CmdRemove, EncodeString(path, MaxPathBytes), TimeSpan.FromSeconds(30), null, cancellationToken);

    public Task RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken = default) =>
        RequestAsync(CmdRename, [.. EncodeString(oldPath, MaxPathBytes), .. EncodeString(newPath, MaxPathBytes)], null, null, cancellationToken);

    public Task UpdateMetaAsync(string path, PixlFileMeta meta, CancellationToken cancellationToken = default)
    {
        var encoded = EncodeMeta(meta);
        return RequestAsync(CmdUpdateMeta, [.. EncodeString(path, MaxPathBytes), (byte)encoded.Length, .. encoded], null, null, cancellationToken);
    }

    // ---------------------------------------------------------------- Files

    /// <summary>
    /// Downloads a file.
    /// </summary>
    /// <param name="path">Its path, such as "E:/amiibo/link.bin".</param>
    /// <param name="expectedSize">The size from the folder listing, for progress (0 if unknown).</param>
    /// <param name="progress">Receives the bytes received so far.</param>
    public async Task<byte[]> ReadFileAsync(string path, long expectedSize = 0, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var id = await OpenAsync(path, ModeRead, cancellationToken);
            try
            {
                var received = new Progress<int>(bytes => progress?.Report(new TransferProgress(bytes, (int)Math.Max(expectedSize, bytes))));
                return await RequestAsync(CmdReadFile, [id], null, received, cancellationToken);
            }
            finally
            {
                await CloseQuietlyAsync(id);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Creates or replaces a file.
    /// </summary>
    public async Task WriteFileAsync(string path, byte[] data, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var id = await OpenAsync(path, ModeWrite, cancellationToken);
            var closed = false;
            try
            {
                // One stream: every frame carries the file id; the firmware writes each as it arrives and answers once,
                // after the last (the one without 0x8000), with the outcome of them all.
                var frames = BuildWriteFrames(id, data, _framePayload);
                progress?.Report(new TransferProgress(0, data.Length));
                var sent = new Progress<int>(frame => progress?.Report(new TransferProgress(Math.Min(data.Length, frame * (_framePayload - 1)), data.Length)));
                await ExchangeAsync(CmdWriteFile, frames, TimeSpan.FromSeconds(15), null, sent, cancellationToken);
                progress?.Report(new TransferProgress(data.Length, data.Length));

                // Closing commits the file, so its failure matters here.
                closed = true;
                await RequestAsync(CmdCloseFile, [id], null, null, cancellationToken);
            }
            finally
            {
                if (!closed)
                {
                    await CloseQuietlyAsync(id);
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Splits file data into write frames: each payload is the file id then up to <paramref name="maxPayload"/> - 1
    /// bytes, with 0x8000 on all but the last.
    /// </summary>
    public static IReadOnlyList<byte[]> BuildWriteFrames(byte fileId, ReadOnlySpan<byte> data, int maxPayload = MaxFramePayload)
    {
        var chunk = maxPayload - 1;
        var frames = new List<byte[]>();
        var offset = 0;
        ushort sequence = 0;
        do
        {
            var length = Math.Min(chunk, data.Length - offset);
            var last = offset + length >= data.Length;
            var frame = new byte[HeaderLength + 1 + length];
            frame[0] = CmdWriteFile;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)((sequence & 0x7FFF) | (last ? 0 : MoreChunks)));
            frame[HeaderLength] = fileId;
            data.Slice(offset, length).CopyTo(frame.AsSpan(HeaderLength + 1));
            frames.Add(frame);
            offset += length;
            sequence++;
        }
        while (offset < data.Length);

        return frames;
    }

    /// <summary>
    /// Uploads an amiibo into a folder (the amiibo folder by default) as a 540 byte .bin.
    /// </summary>
    public Task UploadTagAsync(string fileName, TagDump dump, string? folder = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var name = fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ? fileName : $"{fileName}.bin";
        return WriteFileAsync(PixlPath.Combine(folder ?? DefaultFolder, name), dump.ToArray(TagDump.DataLength), progress, cancellationToken);
    }

    /// <summary>
    /// Uploads to the amiibo folder, named after the tag's UID. <paramref name="slot"/> isn't used: the Pixl.js has files.
    /// </summary>
    public override Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        UploadTagAsync(Hex.ToHex(dump.Uid), dump, null, progress, cancellationToken);

    /// <summary>
    /// Restarts into the Nordic DFU bootloader for a firmware update (the device disconnects).
    /// </summary>
    public async Task EnterDfuAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await RequestAsync(CmdEnterDfu, [], TimeSpan.FromSeconds(2), null, cancellationToken);
        }
        catch (TimeoutException)
        {
            // It may reboot before answering.
        }
    }

    private async Task<byte> OpenAsync(string path, uint mode, CancellationToken cancellationToken)
    {
        var flags = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, mode);
        var reply = await RequestAsync(CmdOpenFile, [.. EncodeString(path, MaxPathBytes), .. flags], null, null, cancellationToken);
        if (reply.Length < 1)
        {
            throw new BleException("The Pixl.js didn't return a file handle.");
        }

        return reply[0];
    }

    private async Task CloseQuietlyAsync(byte id)
    {
        try
        {
            await RequestAsync(CmdCloseFile, [id], null, null, CancellationToken.None);
        }
        catch
        {
        }
    }

    // ---------------------------------------------------------------- Framing

    /// <summary>
    /// Sends a one frame request and returns the payload of the reply (which may span frames).
    /// </summary>
    /// <param name="received">Receives the payload bytes collected so far, for long replies.</param>
    private Task<byte[]> RequestAsync(byte command, byte[] payload, TimeSpan? timeout, IProgress<int>? received, CancellationToken cancellationToken)
    {
        if (payload.Length > _framePayload)
        {
            // The firmware doesn't reassemble requests.
            throw new BleException($"That request is too long for the Pixl.js ({payload.Length} bytes, {_framePayload} fit in a frame).");
        }

        return ExchangeAsync(command, BuildFrames(command, payload, _framePayload), timeout, received, null, cancellationToken);
    }

    /// <summary>
    /// Writes frames, then collects the reply message.
    /// </summary>
    private async Task<byte[]> ExchangeAsync(byte command, IReadOnlyList<byte[]> frames, TimeSpan? timeout, IProgress<int>? received, IProgress<int>? sent, CancellationToken cancellationToken)
    {
        var responses = _responses ?? throw new BleException("The Pixl.js isn't connected.");
        var write = _write!;
        var wait = timeout ?? ResponseTimeout;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            responses.Clear();
            for (var i = 0; i < frames.Count; i++)
            {
                await write.WriteAsync(frames[i], true, cancellationToken);
                sent?.Report(i + 1);
            }

            var output = new List<byte>();
            var status = (byte)0;
            var first = true;
            while (true)
            {
                var frame = await responses.ReceiveAsync(wait, cancellationToken);
                if (frame.Length < HeaderLength || frame[0] != command)
                {
                    // A late reply to something earlier, or noise.
                    continue;
                }

                if (first)
                {
                    status = frame[1];
                    first = false;
                }

                output.AddRange(frame.AsSpan(HeaderLength).ToArray());
                received?.Report(output.Count);
                var chunk = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2));
                if ((chunk & MoreChunks) == 0)
                {
                    break;
                }
            }

            if (status != 0)
            {
                throw new PixlException(command, status);
            }

            return output.ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Splits a message into frames: sequence numbers from 0, with 0x8000 on all but the last.
    /// </summary>
    public static IReadOnlyList<byte[]> BuildFrames(byte command, ReadOnlySpan<byte> payload, int maxPayload = MaxFramePayload)
    {
        var frames = new List<byte[]>();
        var offset = 0;
        ushort sequence = 0;
        do
        {
            var length = Math.Min(maxPayload, payload.Length - offset);
            var last = offset + length >= payload.Length;
            var frame = new byte[HeaderLength + length];
            frame[0] = command;
            frame[1] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)((sequence & 0x7FFF) | (last ? 0 : MoreChunks)));
            payload.Slice(offset, length).CopyTo(frame.AsSpan(HeaderLength));
            frames.Add(frame);
            offset += length;
            sequence++;
        }
        while (offset < payload.Length);

        return frames;
    }

    public static byte[] EncodeString(string value, int maxBytes = int.MaxValue)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > maxBytes)
        {
            throw new BleException($"\"{value}\" is too long for the Pixl.js ({bytes.Length} bytes, the limit is {maxBytes}).");
        }

        var output = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)bytes.Length);
        bytes.CopyTo(output, 2);
        return output;
    }

    /// <summary>
    /// Encodes file meta as type/value entries (without the size byte).
    /// </summary>
    public static byte[] EncodeMeta(PixlFileMeta meta)
    {
        var output = new List<byte>();
        if (!string.IsNullOrEmpty(meta.Notes))
        {
            var notes = Encoding.UTF8.GetBytes(meta.Notes);
            if (notes.Length > PixlFileMeta.MaxNotesBytes)
            {
                throw new BleException($"Notes can be up to {PixlFileMeta.MaxNotesBytes} bytes (got {notes.Length}).");
            }

            output.Add(MetaNotes);
            output.Add((byte)notes.Length);
            output.AddRange(notes);
        }

        if (meta.Hidden)
        {
            output.Add(MetaFlags);
            output.Add(FlagHidden);
        }

        if (meta.AmiiboId is { Length: 16 } id)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Convert.ToUInt32(id[..8], 16));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), Convert.ToUInt32(id[8..], 16));
            output.Add(MetaAmiibo);
            output.AddRange(bytes);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Parses file meta. Accepts it with or without a leading size byte; stops at anything it doesn't understand.
    /// </summary>
    public static PixlFileMeta ParseMeta(ReadOnlySpan<byte> meta)
    {
        if (meta.IsEmpty || meta[0] is 0x00 or 0xFF)
        {
            return PixlFileMeta.Empty;
        }

        if (meta.Length > 1 && meta[0] == meta.Length - 1 && meta[1] is MetaNotes or MetaFlags or MetaAmiibo)
        {
            meta = meta[1..];
        }

        string? notes = null;
        var hidden = false;
        string? amiiboId = null;
        var i = 0;
        while (i < meta.Length)
        {
            var type = meta[i++];
            if (type == MetaNotes && i < meta.Length)
            {
                var length = Math.Min(meta[i++], meta.Length - i);
                notes = Encoding.UTF8.GetString(meta.Slice(i, length));
                i += length;
            }
            else if (type == MetaFlags && i < meta.Length)
            {
                hidden = (meta[i++] & FlagHidden) != 0;
            }
            else if (type == MetaAmiibo && i + 8 <= meta.Length)
            {
                var head = BinaryPrimitives.ReadUInt32LittleEndian(meta[i..]);
                var tail = BinaryPrimitives.ReadUInt32LittleEndian(meta[(i + 4)..]);
                amiiboId = $"{head:x8}{tail:x8}";
                i += 8;
            }
            else
            {
                break;
            }
        }

        return new PixlFileMeta(notes, hidden, amiiboId);
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
    /// Reads the little endian fields of a reply.
    /// </summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public int Remaining => _data.Length - _offset;

        public byte Byte() => Take(1)[0];

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public byte[] Bytes(int count) => Take(count).ToArray();

        public string String()
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            return Encoding.UTF8.GetString(Take(length)).TrimEnd('\0');
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > Remaining)
            {
                throw new BleException("The Pixl.js sent a truncated reply.");
            }

            var slice = _data.Slice(_offset, count);
            _offset += count;
            return slice;
        }
    }
}

/// <summary>
/// Pixl.js paths: a drive letter, a colon, then forward slashes ("E:/amiibo/data/link.bin").
/// </summary>
public static class PixlPath
{
    public static string Combine(string folder, string name)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return name;
        }

        return folder.EndsWith('/') ? folder + name : $"{folder}/{name}";
    }

    /// <summary>
    /// The containing folder, or null at a drive's root.
    /// </summary>
    public static string? Parent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0 || IsRoot(path))
        {
            return null;
        }

        var parent = trimmed[..slash];
        return parent.EndsWith(':') ? parent + "/" : parent;
    }

    public static bool IsRoot(string path) => path.Length <= 3 && path.EndsWith(":/", StringComparison.Ordinal);

    public static string GetName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// Why a file or folder name can't be used, or null if it's fine.
    /// </summary>
    public static string? ValidateName(string name, string folder)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name.";
        }

        if (name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return "Names can't contain / \\ or :.";
        }

        if (Encoding.UTF8.GetByteCount(name) > PixlDevice.MaxNameBytes)
        {
            return $"Names can be up to {PixlDevice.MaxNameBytes} bytes.";
        }

        if (Encoding.UTF8.GetByteCount(Combine(folder, name)) > PixlDevice.MaxPathBytes)
        {
            return $"The full path can be up to {PixlDevice.MaxPathBytes} bytes; use a shorter name or folder.";
        }

        return null;
    }
}
