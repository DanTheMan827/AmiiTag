using System.Text;
using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// AmiLoop. Port of amiloop.html from DanTheMan827/amiibolink-amiloop-webbluetooth.
/// </summary>
public sealed class AmiLoopDevice : UartPacketDeviceBase, ICommandTagDevice
{
    private const int TagLength = 540;
    private const int ChunkSize = 128;

    private static readonly byte[] Handshake = [0x02, 0x01, 0x89, 0x88, 0x03];

    private static readonly Dictionary<string, byte[]> CommandBytes = new()
    {
        ["manual"] = [0x02, 0x02, 0x8A, 0x00, 0x88, 0x03],
        ["sequential"] = [0x02, 0x02, 0x8A, 0x01, 0x89, 0x03],
        ["automatic"] = [0x02, 0x02, 0x8A, 0x02, 0x8A, 0x03],
        // 02 len 8F xor 03, RESET_SETTING. (The original web tool sent this with 12 0D 00 in front, which the Pixl.js
        // emulator rejects as a bad frame; the framed command alone is what the AmiLoop app sends.)
        ["reset"] = [0x02, 0x01, 0x8F, 0x8E, 0x03],
    };

    public AmiLoopDevice(IBleConnection connection) : base(connection)
    {
    }

    public override TagDeviceKind Kind => TagDeviceKind.AmiLoop;

    /// <summary>
    /// How long to wait for the handshake reply during detection.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public IReadOnlyList<TagDeviceCommand> Commands { get; } =
    [
        new("manual", "Manual mode"),
        new("sequential", "Sequential mode"),
        new("automatic", "Automatic mode"),
        new("reset", "Reset AmiLoop", IsDestructive: true),
    ];

    public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await SetupUartAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            var response = await SendAndWaitAsync(Handshake, cancellationToken, HandshakeTimeout);
            if (response.Length < 3 || response[0] != 0x02)
            {
                return false;
            }

            Firmware = ParseVersionText(response);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the version text from a reply framed as 02 len data xor 03. The data is "AmiLoop_FW_V1.0.7" on an
    /// AmiLoop; the Pixl.js emulator sends it with a 16 bit length in front. Only the printable text is kept.
    /// </summary>
    public static string? ParseVersionText(ReadOnlySpan<byte> response)
    {
        if (response.Length < 3 || response[0] != 0x02)
        {
            return null;
        }

        var data = response.Slice(2, Math.Min(response[1], response.Length - 2));
        var text = new StringBuilder();
        foreach (var b in data)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                text.Append((char)b);
            }
        }

        var result = text.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    public override async Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var data = dump.ToArray(TagLength);
        data[536] = 0x80;
        data[537] = 0x80;

        var packets = BuildPackets(data);
        for (var i = 0; i < packets.Count; i++)
        {
            progress?.Report(new TransferProgress(i, packets.Count, "Writing"));
            var reply = await SendAndWaitAsync(packets[i], cancellationToken);

            // 02 01 status xor 03: status 01 means the packet's checksum didn't match.
            if (reply is [0x02, 0x01, 0x01, ..])
            {
                throw new BleException($"The AmiLoop rejected packet {i + 1} of {packets.Count} (bad checksum). Try again.");
            }
        }

        progress?.Report(new TransferProgress(packets.Count, packets.Count, "Writing"));
    }

    /// <summary>
    /// Frames tag data: 02 len 87 last index payload checksum 03, where checksum is the XOR of everything between.
    /// </summary>
    public static List<byte[]> BuildPackets(byte[] data)
    {
        var packets = new List<byte[]>();
        var index = 0;
        for (var offset = 0; offset < data.Length; offset += ChunkSize)
        {
            var chunkLength = Math.Min(ChunkSize, data.Length - offset);
            var packet = new byte[chunkLength + 7];
            packet[0] = 0x02;
            packet[1] = (byte)(chunkLength + 3);
            packet[2] = 0x87;
            packet[3] = (byte)(chunkLength < ChunkSize ? 1 : 0);
            packet[4] = (byte)index++;
            Array.Copy(data, offset, packet, 5, chunkLength);

            byte checksum = 0;
            for (var i = 1; i < packet.Length - 2; i++)
            {
                checksum ^= packet[i];
            }

            packet[^2] = checksum;
            packet[^1] = 0x03;
            packets.Add(packet);
        }

        return packets;
    }

    public async Task<string?> RunCommandAsync(TagDeviceCommand command, CancellationToken cancellationToken = default)
    {
        if (!CommandBytes.TryGetValue(command.Id, out var bytes))
        {
            throw new ArgumentException($"Unknown command {command.Id}.", nameof(command));
        }

        if (command.Id == "reset")
        {
            await SendAsync(bytes, cancellationToken);
        }
        else
        {
            await SendAndWaitAsync(bytes, cancellationToken);
        }

        return null;
    }
}
