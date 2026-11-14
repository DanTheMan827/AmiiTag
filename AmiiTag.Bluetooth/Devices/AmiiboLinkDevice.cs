using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// AmiiboLink. Port of amiibolink.html from DanTheMan827/amiibolink-amiloop-webbluetooth.
/// </summary>
public sealed class AmiiboLinkDevice : UartPacketDeviceBase, ICommandTagDevice
{
    private const int TagLength = 540;
    private const int ChunkSize = 20;

    private static readonly Dictionary<string, byte[]> CommandBytes = new()
    {
        ["auto-random"] = [0x00, 0x00, 0x10, 0x03, 0xE3, 0x96, 0x51, 0xEC, 0x07, 0xE7, 0xE5, 0x54, 0x37, 0xB6, 0x13, 0x8E, 0x80, 0xC9, 0xB3, 0x09],
        ["manual-random"] = [0x00, 0x00, 0x10, 0x03, 0x34, 0x1F, 0x98, 0xE8, 0x46, 0x19, 0x85, 0x75, 0xE3, 0xD3, 0xE0, 0x42, 0x5D, 0x41, 0x89, 0x42],
        ["sequential"] = [0x00, 0x00, 0x10, 0x03, 0x2F, 0x25, 0xD8, 0x0C, 0x49, 0x42, 0xC0, 0xD5, 0x0B, 0x0B, 0xC6, 0xDF, 0xCA, 0x60, 0x21, 0xFC],
        ["factory-reset"] = [0x00, 0x00, 0x10, 0x02, 0xAA, 0x54, 0x54, 0x2B, 0xD5, 0xCC, 0x22, 0x42, 0x36, 0x7D, 0x6D, 0xB2, 0x6A, 0xAC, 0xA6, 0xAC],
        ["info"] = [0x00, 0x00, 0x10, 0x02, 0x33, 0x53, 0x34, 0xAB, 0x1F, 0xE8, 0xC2, 0x6D, 0xE5, 0x35, 0x27, 0x4B, 0x52, 0xE0, 0x1F, 0x26],
    };

    public AmiiboLinkDevice(IBleConnection connection) : base(connection)
    {
    }

    public override TagDeviceKind Kind => TagDeviceKind.AmiiboLink;

    public IReadOnlyList<TagDeviceCommand> Commands { get; } =
    [
        new("auto-random", "Automatic random mode"),
        new("manual-random", "Manual random mode"),
        new("sequential", "Sequential mode"),
        new("info", "Device info"),
        new("factory-reset", "Factory reset", IsDestructive: true),
    ];

    /// <summary>
    /// The AmiiboLink has no handshake, so this only checks the UART service is present.
    /// </summary>
    public override Task<bool> InitializeAsync(CancellationToken cancellationToken = default) => SetupUartAsync(cancellationToken);

    public override async Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var data = dump.ToArray(TagLength);
        var packets = new List<byte[]>
        {
            new byte[] { 0xA0, 0xB0 },
            new byte[] { 0xAC, 0xAC, 0x00, 0x04, 0x00, 0x00, 0x02, 0x1C },
            new byte[] { 0xAB, 0xAB, 0x02, 0x1C },
        };

        for (var offset = 0; offset < TagLength; offset += ChunkSize)
        {
            var packet = new byte[26];
            packet[0] = 0xDD;
            packet[1] = 0xAA;
            packet[2] = 0x00;
            packet[3] = 0x14;
            Array.Copy(data, offset, packet, 4, ChunkSize);
            packet[24] = 0x00;
            packet[25] = (byte)(offset / ChunkSize + 1);
            packets.Add(packet);
        }

        packets.Add([0xBC, 0xBC]);
        packets.Add([0xCC, 0xDD]);

        for (var i = 0; i < packets.Count; i++)
        {
            progress?.Report(new TransferProgress(i, packets.Count, "Writing"));
            await SendAndWaitAsync(packets[i], cancellationToken);
        }

        progress?.Report(new TransferProgress(packets.Count, packets.Count, "Writing"));
    }

    public async Task<string?> RunCommandAsync(TagDeviceCommand command, CancellationToken cancellationToken = default)
    {
        if (!CommandBytes.TryGetValue(command.Id, out var bytes))
        {
            throw new ArgumentException($"Unknown command {command.Id}.", nameof(command));
        }

        var response = await SendAndWaitAsync(bytes, cancellationToken);
        return command.Id == "info" ? Hex.ToSpacedHex(response) : null;
    }
}
