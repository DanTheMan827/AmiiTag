using System.Buffers.Binary;
using System.Text;
using AmiiTag.Bluetooth;
using AmiiTag.Bluetooth.Devices;

namespace AmiiTag.Tests.Simulation;

/// <summary>
/// A connected BLE device with a Nordic UART service, for driver tests. Subclasses answer what the central writes.
/// </summary>
public abstract class SimulatedUartDevice : IBleConnection
{
    private readonly Characteristic _write;
    private readonly Characteristic _notify;

    protected SimulatedUartDevice(string name)
    {
        Device = new BleDeviceInfo(Guid.NewGuid().ToString(), name, []);
        _write = new Characteristic(BleUuids.NordicUartWrite, this);
        _notify = new Characteristic(BleUuids.NordicUartNotify, this);
    }

    public BleDeviceInfo Device { get; }

    public bool IsConnected { get; private set; } = true;

    /// <summary>
    /// The MTU reported to the driver (0 = unknown, like Web Bluetooth).
    /// </summary>
    public int Mtu { get; init; } = 247;

    /// <summary>
    /// Every write from the central, in order.
    /// </summary>
    public List<byte[]> Received { get; } = [];

    public bool NotificationsStarted { get; private set; }

#pragma warning disable CS0067
    public event EventHandler? Disconnected;
#pragma warning restore CS0067

    public Task<bool> HasServiceAsync(Guid service, CancellationToken cancellationToken = default) =>
        Task.FromResult(service == BleUuids.NordicUartService);

    public Task<IBleCharacteristic?> GetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken = default)
    {
        if (service != BleUuids.NordicUartService)
        {
            return Task.FromResult<IBleCharacteristic?>(null);
        }

        return Task.FromResult<IBleCharacteristic?>(characteristic == BleUuids.NordicUartWrite ? _write : characteristic == BleUuids.NordicUartNotify ? _notify : null);
    }

    public Task<int> RequestMtuAsync(int mtu, CancellationToken cancellationToken = default) => Task.FromResult(Mtu);

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    /// <summary>
    /// Sends a notification to the central.
    /// </summary>
    protected void Notify(byte[] data) => _notify.Raise(data);

    protected abstract void OnWrite(byte[] data);

    /// <summary>
    /// Called when the central subscribes to the notify characteristic (the first time).
    /// </summary>
    protected virtual void OnNotificationsStarted()
    {
    }

    private sealed class Characteristic : IBleCharacteristic
    {
        private readonly SimulatedUartDevice _owner;

        public Characteristic(Guid id, SimulatedUartDevice owner)
        {
            Id = id;
            _owner = owner;
        }

        public Guid Id { get; }

        public event EventHandler<byte[]>? ValueUpdated;

        public void Raise(byte[] data) => ValueUpdated?.Invoke(this, data);

        public Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default)
        {
            if (Id != BleUuids.NordicUartWrite)
            {
                throw new BleException("Not writable.");
            }

            if (_owner.Mtu > 0 && data.Length > _owner.Mtu - 3)
            {
                throw new BleException($"A {data.Length} byte write doesn't fit an MTU of {_owner.Mtu}.");
            }

            _owner.Received.Add(data);
            _owner.OnWrite(data);
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Array.Empty<byte>());

        public Task StartNotificationsAsync(CancellationToken cancellationToken = default)
        {
            if (Id == BleUuids.NordicUartNotify && !_owner.NotificationsStarted)
            {
                _owner.NotificationsStarted = true;
                _owner.OnNotificationsStarted();
            }

            return Task.CompletedTask;
        }

        public Task StopNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>
/// A Pixl.js following its firmware (fw/application/src/mod/df): every frame is a request on its own, except file
/// writes, which stream frames and answer once after the last; one file open at a time; a small file system.
/// </summary>
public sealed class PixlSimulator : SimulatedUartDevice
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _meta = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { "E:/" };
    private (string Path, bool Write)? _open;
    private bool _writeFailed;

    public PixlSimulator(string name = "Pixl.js") : base(name)
    {
    }

    public string Version { get; set; } = "2.11.0";

    public string? Address { get; set; } = "C3:5B:9E:11:22:33";

    public long DriveSize { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Commands answered with 0xFF (unsupported), as old firmware would.
    /// </summary>
    public HashSet<byte> Unsupported { get; } = [];

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public IReadOnlyCollection<string> Folders => _folders;

    public IReadOnlyDictionary<string, byte[]> Meta => _meta;

    public int OpenHandles => _open == null ? 0 : 1;

    /// <summary>
    /// Requests that arrived as more than one frame (the firmware would have handled each frame on its own).
    /// </summary>
    public int SplitRequests { get; private set; }

    public bool EnteredDfu { get; private set; }

    /// <summary>
    /// Every complete request message (command and payload).
    /// </summary>
    public List<(byte Command, byte[] Payload)> Requests { get; } = [];

    public void AddFile(string path, byte[] data, byte[]? meta = null)
    {
        _files[path] = data;
        if (meta != null)
        {
            _meta[path] = meta;
        }
    }

    public void AddFolder(string path) => _folders.Add(path);

    private int MaxPayload => Math.Min(243, (Mtu > 0 ? Mtu : 247) - 3 - 4);

    protected override void OnWrite(byte[] data)
    {
        if (data.Length < 4)
        {
            return;
        }

        var command = data[0];
        var chunk = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var payload = data[4..];
        var more = (chunk & 0x8000) != 0;

        if (command == 0x15)
        {
            // A write stream: write each frame, answer after the last.
            if ((chunk & 0x7FFF) == 0)
            {
                _writeFailed = false;
            }

            if (_open is { Write: true } file && !_writeFailed)
            {
                _files[file.Path] = [.. _files[file.Path], .. payload[1..]];
            }
            else
            {
                _writeFailed = true;
            }

            Requests.Add((command, payload));
            if (!more)
            {
                SendReply(command, (byte)(_writeFailed ? 1 : 0), []);
            }

            return;
        }

        if (more || (chunk & 0x7FFF) != 0)
        {
            SplitRequests++;
        }

        Requests.Add((command, payload));
        var (status, reply) = Handle(command, payload);
        SendReply(command, status, reply);
    }

    private void SendReply(byte command, byte status, byte[] reply)
    {
        var offset = 0;
        ushort sequence = 0;
        do
        {
            var length = Math.Min(MaxPayload, reply.Length - offset);
            var last = offset + length >= reply.Length;
            var frame = new byte[4 + length];
            frame[0] = command;
            frame[1] = status;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)(sequence | (last ? 0 : 0x8000)));
            reply.AsSpan(offset, length).CopyTo(frame.AsSpan(4));
            Notify(frame);
            offset += length;
            sequence++;
        }
        while (offset < reply.Length);
    }

    private (byte Status, byte[] Reply) Handle(byte command, byte[] payload)
    {
        if (Unsupported.Contains(command))
        {
            return (0xFF, []);
        }

        var reader = new Reader(payload);
        switch (command)
        {
            case 0x01:
                if (Address == null)
                {
                    return (0, Str(Version));
                }

                return (0, [.. Str(Version), .. Str(Address)]);

            case 0x02:
                EnteredDfu = true;
                return (0, []);

            case 0x10:
            {
                var used = _files.Values.Sum(f => (long)f.Length);
                return (0, [1, 0, (byte)'E', .. Str("External Flash"), .. U32((uint)DriveSize), .. U32((uint)(DriveSize - used))]);
            }

            case 0x11:
                if (reader.Byte() != 'E')
                {
                    return (1, []);
                }

                _files.Clear();
                _meta.Clear();
                _folders.Clear();
                _folders.Add("E:/");
                return (0, []);

            case 0x12:
            {
                // Opening closes whatever was open.
                _open = null;
                var path = reader.String();
                var flags = reader.UInt32();
                var write = (flags & 0x10) != 0;
                if ((!write || (flags & 0x04) == 0) && !_files.ContainsKey(path))
                {
                    return (1, []);
                }

                if (!_folders.Contains(ParentOf(path)))
                {
                    return (1, []);
                }

                if (write && (flags & 0x02) != 0)
                {
                    _files[path] = [];
                }

                _open = (path, write);
                return (0, [0]);
            }

            case 0x13:
            {
                reader.Byte();
                if (_open == null)
                {
                    return (1, []);
                }

                _open = null;
                return (0, []);
            }

            case 0x14:
            {
                reader.Byte();
                if (_open is not { Write: false } file)
                {
                    return (1, []);
                }

                return (0, _files[file.Path]);
            }

            case 0x16:
            {
                var path = reader.String().TrimEnd('/');
                var folder = path.EndsWith(':') ? path + "/" : path;
                if (!_folders.Contains(folder))
                {
                    return (1, []);
                }

                var output = new List<byte>();
                foreach (var sub in _folders.Where(f => f != folder && ParentOf(f) == folder))
                {
                    output.AddRange([.. Str(NameOf(sub)), .. U32(0), 1, 0]);
                }

                foreach (var (filePath, data) in _files.Where(f => ParentOf(f.Key) == folder))
                {
                    var meta = _meta.GetValueOrDefault(filePath) ?? [];
                    output.AddRange([.. Str(NameOf(filePath)), .. U32((uint)data.Length), 0, (byte)meta.Length, .. meta]);
                }

                return (0, output.ToArray());
            }

            case 0x17:
            {
                var path = reader.String();
                if (!_folders.Contains(ParentOf(path)))
                {
                    return (1, []);
                }

                _folders.Add(path);
                return (0, []);
            }

            case 0x18:
            {
                var path = reader.String();
                if (_files.Remove(path))
                {
                    _meta.Remove(path);
                    return (0, []);
                }

                if (!_folders.Remove(path))
                {
                    return (1, []);
                }

                foreach (var key in _files.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                {
                    _files.Remove(key);
                }

                _folders.RemoveWhere(f => f.StartsWith(path + "/", StringComparison.Ordinal));
                return (0, []);
            }

            case 0x19:
            {
                var from = reader.String();
                var to = reader.String();
                if (_files.Remove(from, out var data))
                {
                    _files[to] = data;
                    if (_meta.Remove(from, out var meta))
                    {
                        _meta[to] = meta;
                    }

                    return (0, []);
                }

                if (!_folders.Remove(from))
                {
                    return (1, []);
                }

                _folders.Add(to);
                foreach (var key in _files.Keys.Where(k => k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
                {
                    _files.Remove(key, out var moved);
                    _files[to + key[from.Length..]] = moved!;
                }

                return (0, []);
            }

            case 0x1A:
            {
                var path = reader.String();
                var size = reader.Byte();
                if (!_files.ContainsKey(path))
                {
                    return (1, []);
                }

                _meta[path] = reader.Take(size);
                return (0, []);
            }
        }

        return (0xFF, []);
    }

    private static string ParentOf(string path)
    {
        var slash = path.TrimEnd('/').LastIndexOf('/');
        if (slash < 0)
        {
            return path;
        }

        var parent = path[..slash];
        return parent.EndsWith(':') ? parent + "/" : parent;
    }

    private static string NameOf(string path) => path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];

    public static byte[] Str(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [(byte)bytes.Length, (byte)(bytes.Length >> 8), .. bytes];
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private sealed class Reader(byte[] data)
    {
        private int _offset;

        public byte Byte() => data[_offset++];

        public uint UInt32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_offset));
            _offset += 4;
            return value;
        }

        public byte[] Take(int count)
        {
            var slice = data.AsSpan(_offset, count).ToArray();
            _offset += count;
            return slice;
        }

        public string String()
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_offset));
            _offset += 2;
            return Encoding.UTF8.GetString(Take(length));
        }

        public byte[] Rest() => Take(data.Length - _offset);
    }
}

/// <summary>
/// An AmiiboLink as the Pixl.js emulates it (mod/ble/ble_amiibolink.c): V1 takes and sends plain packets; V2 takes
/// packets of 16 bytes or more as AES packets (shorter ones as plain) and always answers encrypted, with the sender's keys.
/// </summary>
public sealed class AmiiboLinkSimulator : SimulatedUartDevice
{
    private readonly byte[] _tag = new byte[540];
    private int _position;
    private byte _key1;
    private byte _key2;

    public AmiiboLinkSimulator(AmiiboLinkProtocol protocol = AmiiboLinkProtocol.V2, string name = "") : base(name)
    {
        Protocol = protocol;
    }

    public AmiiboLinkProtocol Protocol { get; }

    public int Mode { get; private set; } = 1;

    /// <summary>
    /// The tag applied by the last complete write.
    /// </summary>
    public byte[]? AppliedTag { get; private set; }

    /// <summary>
    /// Every command after decryption.
    /// </summary>
    public List<byte[]> Commands { get; } = [];

    /// <summary>
    /// Packets a V2 device couldn't decrypt into a known command.
    /// </summary>
    public int Garbled { get; private set; }

    protected override void OnWrite(byte[] data)
    {
        byte[] plain;
        if (Protocol == AmiiboLinkProtocol.V2 && data.Length >= 16)
        {
            if (!AmiiboLinkDevice.TryDecryptPacket(data, out plain))
            {
                Garbled++;
                return;
            }

            _key1 = data[0];
            _key2 = data[1];
        }
        else
        {
            plain = data;
        }

        if (plain.Length < 2)
        {
            return;
        }

        Commands.Add(plain);
        switch ((plain[0], plain[1]))
        {
            case (0xA2, 0xB2):
            {
                var version = Protocol == AmiiboLinkProtocol.V1 ? "0.0.4" : "1.2.1.33";
                byte[] reply = [0xB2, 0xA2, (byte)version.Length, .. System.Text.Encoding.ASCII.GetBytes(version), 1, (byte)Mode, 4, 0xDE, 0xAD, 0xBE, 0xEF, 0];
                Send(reply);
                break;
            }

            case (0xA1, 0xB1):
                Mode = plain[2];
                Send([0xB1, 0xA1]);
                break;
            case (0xA0, 0xB0):
                Send([0xB0, 0xA0]);
                break;
            case (0xAC, 0xAC):
                _position = 0;
                Send([0xCA, 0xCA]);
                break;
            case (0xAB, 0xAB):
                Send([0xBA, 0xBA]);
                break;
            case (0xDD, 0xAA):
                var size = plain[3];
                plain.AsSpan(4, size).CopyTo(_tag.AsSpan(_position));
                _position += size;
                Send([0xAA, 0xDD]);
                break;
            case (0xBC, 0xBC):
                Send([0xCB, 0xCB]);
                break;
            case (0xCC, 0xDD):
                AppliedTag = (byte[])_tag.Clone();
                Send([0xDD, 0xCC]);
                break;
            default:
                Garbled++;
                break;
        }
    }

    private void Send(byte[] plain) =>
        Notify(Protocol == AmiiboLinkProtocol.V1 ? plain : Pad36(AmiiboLinkDevice.EncryptPacket(plain, _key1, _key2)));

    // The firmware always sends the whole 36 byte structure.
    private static byte[] Pad36(byte[] packet) => packet.Length >= 36 ? packet : [.. packet, .. new byte[36 - packet.Length]];
}

/// <summary>
/// A puck running Espruino with the DTM puck firmware, enough for detection: the banner Espruino prints when a central
/// subscribes, the fast mode switch, and a few info commands.
/// </summary>
public sealed class EspruinoPuckSimulator : SimulatedUartDevice
{
    public const string Banner = "\r\n ____                 _\r\n|  __|___ ___ ___ _ _|_|___ ___\r\n|  __|_ -| . |  _| | | |   | . |\r\n|____|___|  _|_| |___|_|_|_|___|\r\n         |_| espruino.com\r\n 2v19 (c) 2021 G.Williams\r\n>";

    private bool _fastMode;

    public EspruinoPuckSimulator(string name = "Puck.js 1234", bool sendBanner = true) : base(name)
    {
        SendBanner = sendBanner;
    }

    public bool SendBanner { get; }

    protected override void OnNotificationsStarted()
    {
        if (SendBanner)
        {
            foreach (var chunk in Encoding.ASCII.GetBytes(Banner).Chunk(20))
            {
                Notify(chunk);
            }
        }
    }

    protected override void OnWrite(byte[] data)
    {
        if (!_fastMode)
        {
            if (Encoding.ASCII.GetString(data).Contains("fastMode()", StringComparison.Ordinal))
            {
                _fastMode = true;
                Notify(Encoding.ASCII.GetBytes("DTM_PUCK_FAST\r\n"));
            }

            return;
        }

        switch (data[0])
        {
            case 0xFC:
                Notify([0xFC, .. "test"u8, 0]);
                break;
            case 0xFA:
                Notify([0xFA, .. "Puck.js 1234"u8, 0]);
                break;
            case 0xEE:
                Notify([0xEE]);
                break;
        }
    }
}
