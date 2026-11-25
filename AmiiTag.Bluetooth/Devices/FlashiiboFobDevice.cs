using System.Text;
using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// Flashiibo Fob, using its acknowledged packet protocol over Nordic UART.
/// </summary>
public sealed class FlashiiboFobDevice : UartPacketDeviceBase, ICommandTagDevice
{
    private const int TagLength = 540;

    private static readonly byte[] Handshake = [0x02, 0x01, 0x89, 0x88, 0x03];

    private static readonly Dictionary<string, byte[]> CommandBytes = new()
    {
        ["manual"] = [0x02, 0x02, 0x8A, 0x00, 0x88, 0x03],
        ["automatic"] = [0x02, 0x02, 0x8A, 0x02, 0x8A, 0x03],
        // The three-byte prefix is part of the Fob companion app's factory-reset command.
        ["reset"] = [0x12, 0x0D, 0x00, 0x02, 0x01, 0x8F, 0x8E, 0x03],
    };

    public FlashiiboFobDevice(IBleConnection connection) : base(connection)
    {
    }

    public override TagDeviceKind Kind => TagDeviceKind.FlashiiboFob;

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public IReadOnlyList<TagDeviceCommand> Commands { get; } =
    [
        new("manual", "Manual mode"),
        new("automatic", "Automatic mode"),
        new("reset", "Factory reset Flashiibo Fob", IsDestructive: true),
    ];

    public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!await SetupUartAsync(cancellationToken, 517))
        {
            return false;
        }

        try
        {
            var response = await SendAndWaitAsync(Handshake, cancellationToken, HandshakeTimeout, requireNonEmpty: true);

            Firmware = ParseFirmware(response);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static string? ParseFirmware(ReadOnlySpan<byte> response)
    {
        if (response.Length < 4)
        {
            return null;
        }

        var end = Math.Min(response[1] + 3, response.Length);
        var value = Encoding.UTF8.GetString(response[3..end]).Replace("AmiLoop_FW_", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(value) ? null : value;
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
            await SendAndWaitAsync(packets[i], cancellationToken, TimeSpan.FromSeconds(15), requireNonEmpty: true);
        }

        progress?.Report(new TransferProgress(packets.Count, packets.Count, "Writing"));
    }

    /// <summary>
    /// Frames tag data as 02 LEN 87 LAST INDEX DATA XOR 03.
    /// </summary>
    public static IReadOnlyList<byte[]> BuildPackets(byte[] data) => AmiLoopDevice.BuildPackets(data);

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
            await SendAndWaitAsync(bytes, cancellationToken, TimeSpan.FromSeconds(15), requireNonEmpty: true);
        }

        return null;
    }
}
