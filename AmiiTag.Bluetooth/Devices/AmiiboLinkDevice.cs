using System.Security.Cryptography;
using System.Text;
using AmiiTag.Tags;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// The two AmiiboLink protocols. V2 wraps every packet in AES; V1 sends them as they are.
/// </summary>
public enum AmiiboLinkProtocol
{
    /// <summary>
    /// The device didn't answer the version request. Everything goes V2 encrypted: the original web tool's commands
    /// were V2 packets, so a device that took those speaks V2 (the tool's unencrypted tag data was the odd one out).
    /// </summary>
    Unknown = 0,

    V1 = 1,
    V2 = 2,
}

/// <summary>
/// AmiiboLink (and compatibles such as a Pixl.js in AmiiboLink mode). Originally ported from amiibolink.html in
/// DanTheMan827/amiibolink-amiloop-webbluetooth, checked against the Pixl.js emulator (mod/ble/ble_amiibolink.c).
/// </summary>
/// <remarks>
/// <para>Commands are a 16 bit little endian code plus arguments, on the Nordic UART service:</para>
/// <code>
/// A2 B2            get version   → B2 A2, u8 length version, 01, u8 mode, 04, device id[4], 00
/// A1 B1 mode       set mode      → B1 A1 (01 random, 02 sequential, 03 read/write, 04 random with auto UID)
/// A3 B3            factory reset
/// A0 B0            write: start  → B0 A0
/// AC AC 00 04 00 00 02 1C        → CA CA (540 bytes follow)
/// AB AB 02 1C                    → BA BA
/// DD AA 00 size data… seq        → AA DD (data in order)
/// BC BC                          → CB CB
/// CC DD            write: apply  → DD CC
/// </code>
/// <para>V2 sends each of these encrypted (AES-128 ECB, PKCS#7 padded) as key1 key2 | encrypted length | plain length |
/// data[32], with the key "KGF_AMIL" 07 E7 04 06 0A 2A key1 key2; key1/key2 are chosen by the sender and echoed in
/// replies. Packets under 16 bytes are taken as plain even in V2. V2 devices advertise the service as …180F.</para>
/// </remarks>
public sealed class AmiiboLinkDevice : UartPacketDeviceBase, ICommandTagDevice
{
    private const int TagLength = 540;

    // Tag bytes per data packet: V2 packets hold 31 plain bytes (DD AA 00 n data seq16), so 20; V1 devices take
    // the 150 the official app sends (0x96 in captures) when the link has room for it.
    private const int V2ChunkSize = 20;
    private const int V1ChunkSize = 150;
    private const int V2DataLength = 32;

    /// <summary>
    /// What V2 AmiiboLinks advertise: the Nordic UART UUID with its first group replaced by 0000180F.
    /// </summary>
    public static readonly Guid V2AdvertisedService = Guid.Parse("0000180f-b5a3-f393-e0a9-e50e24dcca9e");

    private static readonly byte[] KeyPrefix = [0x4B, 0x47, 0x46, 0x5F, 0x41, 0x4D, 0x49, 0x4C, 0x07, 0xE7, 0x04, 0x06, 0x0A, 0x2A];

    private static readonly Dictionary<string, byte[]> CommandBytes = new()
    {
        ["manual-random"] = [0xA1, 0xB1, 0x01],
        ["sequential"] = [0xA1, 0xB1, 0x02],
        ["read-write"] = [0xA1, 0xB1, 0x03],
        ["auto-random"] = [0xA1, 0xB1, 0x04],
        ["info"] = [0xA2, 0xB2],
        ["factory-reset"] = [0xA3, 0xB3],
    };

    public AmiiboLinkDevice(IBleConnection connection) : base(connection)
    {
    }

    public override TagDeviceKind Kind => TagDeviceKind.AmiiboLink;

    /// <summary>
    /// The protocol in use, worked out when connecting.
    /// </summary>
    public AmiiboLinkProtocol Protocol { get; private set; } = AmiiboLinkProtocol.Unknown;

    /// <summary>
    /// The emulation mode the device reported (see the set mode command), or null.
    /// </summary>
    public int? Mode { get; private set; }

    public string? DeviceId { get; private set; }

    /// <summary>
    /// How long to wait for the version reply when connecting.
    /// </summary>
    public TimeSpan VersionTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    public IReadOnlyList<TagDeviceCommand> Commands { get; } =
    [
        new("manual-random", "Random mode (manual)"),
        new("auto-random", "Random mode (automatic)"),
        new("sequential", "Sequential mode"),
        new("read-write", "Read/write mode"),
        new("info", "Device info"),
        new("factory-reset", "Factory reset", IsDestructive: true),
    ];

    /// <summary>
    /// Used when the device was identified by name or chosen by the user: sets up the UART and works out the protocol
    /// (by asking for the version, or from the advertisement).
    /// </summary>
    public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await SetupUartAsync(cancellationToken))
        {
            return false;
        }

        await IdentifyAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// For devices with no telling name: like <see cref="InitializeAsync"/>, but only true if the device answers the
    /// version request like an AmiiboLink.
    /// </summary>
    public async Task<bool> ProbeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!await SetupUartAsync(cancellationToken))
        {
            return false;
        }

        return await IdentifyAsync(cancellationToken, timeout);
    }

    /// <summary>
    /// Asks for the version: first in plain form (understood by both protocols; a V2 device answers encrypted), then
    /// encrypted for devices that only take V2 packets.
    /// </summary>
    private async Task<bool> IdentifyAsync(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var wait = timeout ?? VersionTimeout;
        Protocol = Connection.Device.AdvertisedServices.Contains(V2AdvertisedService) ? AmiiboLinkProtocol.V2 : AmiiboLinkProtocol.Unknown;

        foreach (var encrypted in new[] { false, true })
        {
            try
            {
                var packet = encrypted ? EncryptPacket(CommandBytes["info"]) : CommandBytes["info"];
                var reply = await SendAndWaitAsync(packet, cancellationToken, wait);
                if (TryParseReply(reply, out var plain, out var wasEncrypted) && plain is [0xB2, 0xA2, ..])
                {
                    Protocol = wasEncrypted ? AmiiboLinkProtocol.V2 : AmiiboLinkProtocol.V1;
                    ParseVersion(plain);
                    return true;
                }
            }
            catch (TimeoutException)
            {
            }
        }

        return false;
    }

    private void ParseVersion(byte[] reply)
    {
        // B2 A2, u8 length version, 01, mode, 04, id[4], 00
        if (reply.Length < 3 || reply.Length < 3 + reply[2])
        {
            return;
        }

        var version = Encoding.ASCII.GetString(reply, 3, reply[2]);
        Firmware = $"{version} ({Protocol})";
        var offset = 3 + reply[2];
        if (reply.Length >= offset + 2)
        {
            Mode = reply[offset + 1];
        }

        if (reply.Length >= offset + 3 + 4 && reply[offset + 2] == 4)
        {
            DeviceId = Hex.ToHex(reply.AsSpan(offset + 3, 4));
        }
    }

    public override async Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var chunkSize = Protocol == AmiiboLinkProtocol.V1 && NegotiatedMtu > 0
            ? Math.Clamp(NegotiatedMtu - 3 - 6, V2ChunkSize, V1ChunkSize)
            : V2ChunkSize;

        var packets = BuildWritePackets(dump.ToArray(TagLength), chunkSize);
        for (var i = 0; i < packets.Count; i++)
        {
            progress?.Report(new TransferProgress(i, packets.Count, "Writing"));
            var reply = await SendAndWaitAsync(Wrap(packets[i]), cancellationToken);

            // Each step has its own answer (A0 B0 → B0 A0, AC AC → CA CA, …); anything else means the write went wrong.
            var expected = ExpectedReply(packets[i]);
            if (!TryParseReply(reply, out var plain, out _) || plain[0] != expected.Item1 || plain[1] != expected.Item2)
            {
                throw new BleException($"The AmiiboLink answered step {i + 1} of {packets.Count} with {Hex.ToSpacedHex(reply)} instead of {expected.Item1:X2} {expected.Item2:X2}. Try again.");
            }
        }

        progress?.Report(new TransferProgress(packets.Count, packets.Count, "Writing"));
    }

    /// <summary>
    /// The answer to each write step.
    /// </summary>
    public static (byte, byte) ExpectedReply(byte[] packet) => (packet[0], packet[1]) switch
    {
        (0xA0, 0xB0) => (0xB0, 0xA0),
        (0xAC, 0xAC) => (0xCA, 0xCA),
        (0xAB, 0xAB) => (0xBA, 0xBA),
        (0xDD, 0xAA) => (0xAA, 0xDD),
        (0xBC, 0xBC) => (0xCB, 0xCB),
        (0xCC, 0xDD) => (0xDD, 0xCC),
        _ => (packet[1], packet[0]),
    };

    /// <summary>
    /// The plain packets of a write: start, size, the data (DD AA 00 n data, then a 16 bit sequence number from 0),
    /// end and apply.
    /// </summary>
    public static List<byte[]> BuildWritePackets(byte[] data, int chunkSize = V2ChunkSize)
    {
        var packets = new List<byte[]>
        {
            new byte[] { 0xA0, 0xB0 },
            new byte[] { 0xAC, 0xAC, 0x00, 0x04, 0x00, 0x00, (byte)(data.Length >> 8), (byte)data.Length },
            new byte[] { 0xAB, 0xAB, (byte)(data.Length >> 8), (byte)data.Length },
        };

        var sequence = 0;
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - offset);
            var packet = new byte[4 + length + 2];
            packet[0] = 0xDD;
            packet[1] = 0xAA;
            packet[2] = 0x00;
            packet[3] = (byte)length;
            Array.Copy(data, offset, packet, 4, length);
            packet[^2] = (byte)(sequence >> 8);
            packet[^1] = (byte)sequence;
            sequence++;
            packets.Add(packet);
        }

        packets.Add([0xBC, 0xBC]);
        packets.Add([0xCC, 0xDD]);
        return packets;
    }

    public async Task<string?> RunCommandAsync(TagDeviceCommand command, CancellationToken cancellationToken = default)
    {
        if (!CommandBytes.TryGetValue(command.Id, out var bytes))
        {
            throw new ArgumentException($"Unknown command {command.Id}.", nameof(command));
        }

        var reply = await SendAndWaitAsync(Wrap(bytes), cancellationToken);
        if (command.Id != "info")
        {
            if (command.Id.Contains("random") || command.Id is "sequential" or "read-write")
            {
                Mode = bytes[2];
            }

            return null;
        }

        if (!TryParseReply(reply, out var plain, out _) || plain is not [0xB2, 0xA2, ..])
        {
            return Hex.ToSpacedHex(reply);
        }

        ParseVersion(plain);
        var lines = new List<string> { $"Firmware: {Firmware}" };
        if (Mode is int mode)
        {
            lines.Add($"Mode: {DescribeMode(mode)}");
        }

        if (DeviceId != null)
        {
            lines.Add($"Device ID: {DeviceId}");
        }

        return string.Join("\n", lines);
    }

    public static string DescribeMode(int mode) => mode switch
    {
        1 => "Random (manual)",
        2 => "Sequential",
        3 => "Read/write",
        4 => "Random (automatic)",
        _ => $"Unknown ({mode})",
    };

    // ---------------------------------------------------------------- V2 packets

    /// <summary>
    /// Plain for V1; V2 encrypted otherwise (including when the protocol couldn't be worked out).
    /// </summary>
    private byte[] Wrap(byte[] plain) => Protocol == AmiiboLinkProtocol.V1 ? plain : EncryptPacket(plain);

    /// <summary>
    /// key1 key2 | encrypted length | plain length | AES-128 ECB data, PKCS#7 padded (as the official app does), in a
    /// 36 byte packet.
    /// </summary>
    public static byte[] EncryptPacket(ReadOnlySpan<byte> plain, byte? key1 = null, byte? key2 = null)
    {
        if (plain.Length >= V2DataLength)
        {
            throw new ArgumentException($"A V2 packet holds up to {V2DataLength - 1} bytes.", nameof(plain));
        }

        var k1 = key1 ?? (byte)RandomNumberGenerator.GetInt32(256);
        var k2 = key2 ?? (byte)RandomNumberGenerator.GetInt32(256);
        var padded = plain.Length < 16 ? 16 : 32;
        var buffer = new byte[padded];
        plain.CopyTo(buffer);
        buffer.AsSpan(plain.Length).Fill((byte)(padded - plain.Length));

        var packet = new byte[4 + V2DataLength];
        packet[0] = k1;
        packet[1] = k2;
        packet[2] = (byte)padded;
        packet[3] = (byte)plain.Length;
        Crypt(true, k1, k2, buffer).CopyTo(packet, 4);
        return padded == 16 ? packet[..20] : packet;
    }

    /// <summary>
    /// Decrypts a V2 packet. Returns false if it isn't one.
    /// </summary>
    public static bool TryDecryptPacket(ReadOnlySpan<byte> packet, out byte[] plain)
    {
        plain = [];
        if (packet.Length < 20)
        {
            return false;
        }

        var length = packet[2];
        var plainLength = packet[3];
        if (length is not (16 or 32) || packet.Length < 4 + length || plainLength > length)
        {
            return false;
        }

        plain = Crypt(false, packet[0], packet[1], packet.Slice(4, length).ToArray())[..plainLength];
        return true;
    }

    /// <summary>
    /// A reply in either protocol: plain (a short packet starting with a reply code) or V2.
    /// </summary>
    private static bool TryParseReply(byte[] reply, out byte[] plain, out bool encrypted)
    {
        // V2 packets are tried first: their header bytes are random, so they could start like a plain reply.
        if (TryDecryptPacket(reply, out var decrypted) && IsReplyCode(decrypted))
        {
            encrypted = true;
            plain = decrypted;
            return true;
        }

        encrypted = false;
        plain = reply;
        return IsReplyCode(reply);
    }

    private static bool IsReplyCode(byte[] data) =>
        data.Length >= 2 && (data[0], data[1]) is (0xB2, 0xA2) or (0xB1, 0xA1) or (0xB0, 0xA0) or (0xCA, 0xCA) or
            (0xBA, 0xBA) or (0xAA, 0xDD) or (0xCB, 0xCB) or (0xDD, 0xCC);

    private static byte[] Crypt(bool encrypt, byte key1, byte key2, byte[] data)
    {
        var engine = new AesEngine();
        engine.Init(encrypt, new KeyParameter([.. KeyPrefix, key1, key2]));
        var output = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            engine.ProcessBlock(data, offset, output, offset);
        }

        return output;
    }
}
