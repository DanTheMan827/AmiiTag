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

    public bool ReportUsedCapacity { get; set; }

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
                var capacityValue = ReportUsedCapacity ? used : DriveSize - used;
                return (0, [1, 0, (byte)'E', .. Str("External Flash"), .. U32((uint)DriveSize), .. U32((uint)capacityValue)]);
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
/// An Espruino console: collects what's typed into lines and answers <c>\x10Bluetooth.println(JSON.stringify(expr))</c>
/// lines with <see cref="Evaluate"/>'s JSON, as the console prints it (with a prompt).
/// </summary>
public abstract class EspruinoConsoleSimulator : SimulatedUartDevice
{
    private readonly StringBuilder _line = new();

    protected EspruinoConsoleSimulator(string name) : base(name)
    {
    }

    /// <summary>
    /// Every expression evaluated, in order.
    /// </summary>
    public List<string> Expressions { get; } = [];

    /// <summary>
    /// Feeds console input; returns false once the console no longer reads lines (the puck's binary fast mode).
    /// </summary>
    protected bool TypeIntoConsole(byte[] data)
    {
        foreach (var c in Encoding.Latin1.GetString(data))
        {
            if (c == '\u0003')
            {
                _line.Clear();
            }
            else if (c == '\n')
            {
                var line = _line.ToString();
                _line.Clear();
                if (!OnLine(line))
                {
                    return false;
                }
            }
            else
            {
                _line.Append(c);
            }
        }

        return true;
    }

    protected virtual bool OnLine(string line)
    {
        const string start = "Bluetooth.println(JSON.stringify(";
        var at = line.IndexOf(start, StringComparison.Ordinal);
        if (at < 0 || !line.EndsWith("));", StringComparison.Ordinal))
        {
            return true;
        }

        var expression = line[(at + start.Length)..^3];
        Expressions.Add(expression);
        var reply = Evaluate(expression) ?? "undefined";
        foreach (var chunk in Encoding.Latin1.GetBytes(reply + "\r\n>").Chunk(20))
        {
            Notify(chunk);
        }

        return true;
    }

    /// <summary>
    /// The JSON an expression evaluates to, or null for undefined.
    /// </summary>
    protected abstract string? Evaluate(string expression);
}

/// <summary>
/// A Flask (or Slide) running the Flask app: answers the calls the driver makes from an in-memory tag list.
/// </summary>
public sealed class FlaskSimulator : EspruinoConsoleSimulator
{
    public FlaskSimulator(bool slide = false, string name = "") : base(name)
    {
        IsSlide = slide;
    }

    public bool IsSlide { get; }

    public List<string> Tags { get; } = ["Mario|3zfkle|0"];

    public Dictionary<string, byte[]> Uploaded { get; } = [];

    public int Current { get; set; }

    protected override void OnWrite(byte[] data) => TypeIntoConsole(data);

    protected override string? Evaluate(string expression)
    {
        if (expression.Contains("typeof fastMode", StringComparison.Ordinal))
        {
            return "{\"flask\":true,\"puck\":false}";
        }

        if (expression.StartsWith("typeof tag", StringComparison.Ordinal) || expression == "tag.getList()")
        {
            return System.Text.Json.JsonSerializer.Serialize(Tags);
        }

        if (expression == "screen.getVersion()")
        {
            return IsSlide ? "{\"v\":\"6.5\",\"d\":\"1\"}" : "{\"v\":\"8.2\",\"k\":false}";
        }

        if (expression == "process.version")
        {
            return "\"2v08.56\"";
        }

        if (expression == "tag.get()")
        {
            return CurrentTag();
        }

        if (expression.StartsWith("tag.setTag(", StringComparison.Ordinal))
        {
            var name = System.Text.Json.JsonSerializer.Deserialize<string>(expression["tag.setTag(".Length..expression.IndexOf(')')])!;
            Current = Tags.IndexOf(name);
            return CurrentTag();
        }

        if (expression.StartsWith("tag.upload(", StringComparison.Ordinal))
        {
            // tag.upload("name",i,total,"base64")
            var arguments = expression["tag.upload(".Length..^1];
            var name = System.Text.Json.JsonSerializer.Deserialize<string>(arguments[..(arguments.IndexOf("\",", StringComparison.Ordinal) + 1)])!;
            var data = arguments[(arguments.LastIndexOf(",\"", StringComparison.Ordinal) + 2)..^1];
            Tags.Add(name);
            Uploaded[name] = Convert.FromBase64String(data);
            return "true";
        }

        if (expression.StartsWith("tag.download(", StringComparison.Ordinal))
        {
            var name = System.Text.Json.JsonSerializer.Deserialize<string>(expression["tag.download(".Length..^1])!;
            var bytes = Uploaded.TryGetValue(name, out var stored) ? stored : new byte[540];
            return System.Text.Json.JsonSerializer.Serialize(bytes.Select(b => (int)b));
        }

        if (expression.StartsWith("tag.remove(", StringComparison.Ordinal))
        {
            Tags.Remove(System.Text.Json.JsonSerializer.Deserialize<string>(expression["tag.remove(".Length..^1])!);
            return System.Text.Json.JsonSerializer.Serialize(Tags);
        }

        return null;
    }

    private string CurrentTag() =>
        System.Text.Json.JsonSerializer.Serialize(new { index = Current, name = Current >= 0 && Current < Tags.Count ? Tags[Current] : "", v = false });
}

/// <summary>
/// A puck running Espruino with the DTM puck firmware, enough for detection: a console that reports <c>fastMode</c>,
/// the fast mode switch, and a few info commands. Like a real one, it sends nothing until something is written.
/// </summary>
public sealed class EspruinoPuckSimulator : EspruinoConsoleSimulator
{
    private bool _fastMode;

    public EspruinoPuckSimulator(string name = "Puck.js 1234") : base(name)
    {
    }

    protected override string? Evaluate(string expression) =>
        expression.Contains("typeof fastMode", StringComparison.Ordinal) ? "{\"flask\":false,\"puck\":true}" : null;

    protected override bool OnLine(string line)
    {
        if (line.EndsWith("fastMode()", StringComparison.Ordinal))
        {
            _fastMode = true;
            Notify(Encoding.ASCII.GetBytes("DTM_PUCK_FAST\r\n"));
            return false;
        }

        return base.OnLine(line);
    }

    protected override void OnWrite(byte[] data)
    {
        if (!_fastMode)
        {
            TypeIntoConsole(data);
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

/// <summary>
/// A Flask (or Slide) on the next-gen firmware: not an Espruino console, but a fixed set of commands in the same
/// <c>\x10Bluetooth.println(JSON.stringify(...))</c> framing, answered as flask.run's demo mode does. Anything else
/// gets <c>ERR: unknown command</c> followed by the command echoed back, as the real one does.
/// </summary>
public sealed class FlaskNextGenSimulator : SimulatedUartDevice
{
    private readonly StringBuilder _line = new();
    private int _nextId = 2;

    // Folder IDs are their own sequence (they mustn't collide with the starting folders 2 and 3).
    private int _nextFolderId = 4;

    public FlaskNextGenSimulator(bool slide = false, string name = "") : base(name)
    {
        IsSlide = slide;
    }

    public bool IsSlide { get; }

    public sealed record Tag(int Id, string Name, string Type, string TagId, string Meta, byte[] Data);

    public List<Tag> Tags { get; } =
    [
        new(0, "Mario|3zfkle|0", "215", "000000000e5c0502", "0,0,0,1", new byte[540]),
        new(1, "Card|0|1", "1k", "0000000000000000", "2,0,0,0", new byte[1024]),
    ];

    public List<(int Id, string Name)> Folders { get; } = [(2, "Cards"), (3, "Spare")];

    public int CurrentId { get; set; }

    /// <summary>
    /// Every command received, in order (rejected ones too).
    /// </summary>
    public List<string> Commands { get; } = [];

    public string? Keys { get; private set; }

    protected override void OnWrite(byte[] data)
    {
        foreach (var c in Encoding.Latin1.GetString(data))
        {
            if (c != '\n')
            {
                _line.Append(c);
                continue;
            }

            var line = _line.ToString();
            _line.Clear();
            const string start = "\u0010Bluetooth.println(JSON.stringify(";
            var command = line.StartsWith(start, StringComparison.Ordinal) && line.EndsWith("));", StringComparison.Ordinal)
                ? line[start.Length..^3]
                : line;
            Commands.Add(command);
            var reply = Handle(command);
            var text = reply == null ? $"ERR: unknown command\n{command}\n" : reply + "\n";
            foreach (var chunk in Encoding.Latin1.GetBytes(text).Chunk(20))
            {
                Notify(chunk);
            }
        }
    }

    private string? Handle(string command)
    {
        System.Text.RegularExpressions.Match match;
        switch (command)
        {
            case "screen.getVersion()":
                return Json(new { v = 10, d = IsSlide ? 1 : 0, free_pages = 34, total_pages = 39, effective_used_percent = 12.5 });
            case "process.version":
                return "\"FO1.0\"";
            // Like the real one: no battery command (flask.run's demo mode answers it, the firmware doesn't).
            case "tag.getRichList()":
                return Json(new
                {
                    tags = Tags.Select((t, i) => new { id = t.Id, index = i, name = t.Name, type = t.Type, tagId = t.TagId, meta = t.Meta }),
                    folders = Folders.Select(f => new { id = f.Id, name = f.Name }),
                });
            case "tag.get()":
                return Current();
            case "tag.createBlank()":
                Tags.Add(new(_nextId++, $"Blank||{_nextId}", "215", "0000000000000000", "0,0,0,0", new byte[540]));
                return Memory();
            case "function deleteAll()":
                Tags.Clear();
                return Memory();
        }

        if ((match = Regex(@"^tag\.setById\((\d+)\)$", command)).Success)
        {
            CurrentId = int.Parse(match.Groups[1].Value);
            return Current();
        }

        if ((match = Regex(@"^tag\.deleteById\(([\d,]+)\)$", command)).Success)
        {
            var ids = match.Groups[1].Value.Split(',').Select(int.Parse).ToHashSet();
            Tags.RemoveAll(t => ids.Contains(t.Id));
            return Memory();
        }

        if ((match = Regex(@"^tag\.rename\(""([^""]+)"", ""([^""]+)""\)$", command)).Success)
        {
            var at = Tags.FindIndex(t => t.Name == match.Groups[1].Value);
            if (at >= 0) Tags[at] = Tags[at] with { Name = match.Groups[2].Value };
            return Current();
        }

        if ((match = Regex(@"^tag\.download\(""([^""]+)""\)$", command)).Success)
        {
            var tag = Tags.FirstOrDefault(t => t.Name == match.Groups[1].Value);
            return tag == null ? null : Json(tag.Data.Select(b => (int)b));
        }

        if ((match = Regex(@"^tag\.uploadWithMeta\(""([^""]+)"",(\d+),(\d+),""([^""]+)"",(\d+),(\d+),(\d+)\)$", command)).Success)
        {
            var data = Convert.FromBase64String(match.Groups[4].Value);
            var tagId = data.Length >= 92 ? Convert.ToHexString(data, 84, 8).ToLowerInvariant() : "0000000000000000";
            Tags.Add(new(_nextId++, match.Groups[1].Value, data.Length is >= 532 and <= 574 ? "215" : "1k", tagId,
                $"{match.Groups[5].Value},{match.Groups[7].Value},0,1", data));
            return Memory();
        }

        if ((match = Regex(@"^tag\.setImage\((\d+), ""([^""]+)""\)$", command)).Success)
        {
            // flask.run passes the tag's list index here, not its id.
            var at = int.Parse(match.Groups[1].Value);
            if (at < Tags.Count)
            {
                var meta = Tags[at].Meta.Split(',');
                meta[1] = match.Groups[2].Value;
                Tags[at] = Tags[at] with { Meta = string.Join(",", meta) };
            }

            return Memory();
        }

        if ((match = Regex(@"^tag\.createFolder\(""([^""]+)""\)$", command)).Success)
        {
            Folders.Add((_nextFolderId++, match.Groups[1].Value));
            return Memory();
        }

        if ((match = Regex(@"^folder\.remove\((\d+)\)$", command)).Success)
        {
            Folders.RemoveAll(f => f.Id == int.Parse(match.Groups[1].Value));
            return Memory();
        }

        if ((match = Regex(@"^tag\.move\((\d+), (-?\d+)\)$", command)).Success)
        {
            var folder = Folders.Find(f => f.Id == int.Parse(match.Groups[1].Value));
            Folders.Remove(folder);
            var after = int.Parse(match.Groups[2].Value);
            Folders.Insert(after < 0 ? 0 : Folders.FindIndex(f => f.Id == after) + 1, folder);
            return "{\"success\":true}";
        }

        if ((match = Regex(@"^key\.add\(""([^""]+)""\)$", command)).Success)
        {
            Keys = match.Groups[1].Value;
            return Memory();
        }

        return null;
    }

    private string Current()
    {
        var tag = Tags.FirstOrDefault(t => t.Id == CurrentId) ?? Tags.FirstOrDefault();
        return tag == null ? "{}" : Json(new { name = tag.Name, index = Tags.IndexOf(tag) });
    }

    private static string Memory() => Json(new { free_pages = 33, total_pages = 39 });

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static System.Text.RegularExpressions.Match Regex(string pattern, string input) =>
        System.Text.RegularExpressions.Regex.Match(input, pattern);
}
