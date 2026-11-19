using System.Text;
using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// The original puck firmware with its own service (78290001-...). Responses are read, not notified.
/// Port of the Swift <c>LegacyPuckPeripheral</c>.
/// </summary>
public sealed class LegacyPuckDevice : TagDeviceBase, ISlotTagDevice, IRenamableTagDevice, IUartTagDevice
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IBleCharacteristic? _command;
    private IBleCharacteristic? _response;
    private IBleCharacteristic? _nameCharacteristic;
    private string? _name;

    public LegacyPuckDevice(IBleConnection connection) : base(connection)
    {
    }

    public override TagDeviceKind Kind => TagDeviceKind.LegacyPuck;

    public override string Name => _name ?? base.Name;

    public int MaxNameBytes => 20;

    public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _command = await TryGetCharacteristicAsync(BleUuids.LegacyPuckService, BleUuids.LegacyPuckCommand, cancellationToken);
        _response = await TryGetCharacteristicAsync(BleUuids.LegacyPuckService, BleUuids.LegacyPuckResponse, cancellationToken);
        _nameCharacteristic = await TryGetCharacteristicAsync(BleUuids.LegacyPuckService, BleUuids.LegacyPuckName, cancellationToken);
        if (_command == null || _response == null)
        {
            return false;
        }

        try
        {
            await GetSlotSummaryAsync(cancellationToken);
            return true;
        }
        catch (BleException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a command once, then reads the response characteristic until it holds the answer to that command.
    /// The firmware updates the characteristic from its event loop after acknowledging the write, so an immediate read
    /// can still return the previous answer (the Swift app re-read for the same reason). Resending would restart the
    /// command, so only the read is repeated.
    /// </summary>
    private async Task<byte[]> SendAndReadAsync(byte[] bytes, Func<byte[], bool> isAnswer, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await RequireCommand().WriteAsync(bytes, true, cancellationToken);

            byte[] response = [];
            for (var attempt = 0; attempt < ReadAttempts; attempt++)
            {
                response = await _response!.ReadAsync(cancellationToken);
                if (isAnswer(response))
                {
                    return response;
                }

                await Task.Delay(ReadRetryDelay, cancellationToken);
            }

            throw new BleException($"The puck didn't answer command {bytes[0]:X2} (last response: {Hex.ToSpacedHex(response)}).");
        }
        finally
        {
            _lock.Release();
        }
    }

    private const int ReadAttempts = 20;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(25);

    private async Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await RequireCommand().WriteAsync(bytes, true, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SlotSummary> GetSlotSummaryAsync(CancellationToken cancellationToken = default)
    {
        // A slot summary is exactly 3 bytes (an 82 byte slot information answer also starts with 01).
        var response = await SendAndReadAsync([0x01], r => r.Length == 3 && r[0] == 0x01 && r[1] < r[2], cancellationToken);
        return new SlotSummary(response[1], response[2]);
    }

    public async Task<IReadOnlyList<SlotInfo>> GetSlotsAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var summary = await GetSlotSummaryAsync(cancellationToken);
        var slots = new List<SlotInfo>(summary.Total);
        for (var slot = 0; slot < summary.Total; slot++)
        {
            progress?.Report(new TransferProgress(slot, summary.Total, "Reading slots"));
            slots.Add(await GetSlotInformationAsync(slot, cancellationToken));
        }

        progress?.Report(new TransferProgress(summary.Total, summary.Total, "Reading slots"));
        return slots;
    }

    public async Task<SlotInfo> GetSlotInformationAsync(int slot, CancellationToken cancellationToken = default)
    {
        var data = await SendAndReadAsync([0x01, (byte)slot], r => r.Length == 82 && r[0] == 0x01 && r[1] == slot, cancellationToken);
        return new SlotInfo(slot, DumpFromSlotInfo(data.AsSpan(2)));
    }

    public async Task<TagDump> ReadSlotAsync(int slot, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        const int maxPages = 63;
        const int totalPages = 143;
        var tag = new List<byte>(TagDump.FullLength);

        for (var startPage = 0; startPage < totalPages; startPage += maxPages)
        {
            var count = Math.Min(totalPages - startPage, maxPages);
            progress?.Report(new TransferProgress(startPage * 4, TagDump.FullLength, "Reading"));
            // The answer echoes the command (02 slot start count) before the page data.
            var result = await SendAndReadAsync(
                [0x02, (byte)slot, (byte)startPage, (byte)count],
                r => r.Length >= 4 + count * 4 && r[0] == 0x02 && r[1] == slot && r[2] == startPage && r[3] == count,
                cancellationToken);

            tag.AddRange(result.Skip(4).Take(count * 4));
        }

        progress?.Report(new TransferProgress(TagDump.FullLength, TagDump.FullLength, "Reading"));
        var bytes = tag.ToArray();
        Array.Resize(ref bytes, TagDump.FullLength);
        return new TagDump(bytes);
    }

    public override async Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var data = dump.ToArray(TagDump.FullLength);
        var targetSlot = slot ?? (await GetSlotSummaryAsync(cancellationToken)).Current;

        for (var startPage = 0; startPage * 4 < data.Length; startPage += 4)
        {
            var offset = startPage * 4;
            var chunk = data.AsSpan(offset, Math.Min(16, data.Length - offset)).ToArray();
            progress?.Report(new TransferProgress(offset, data.Length, "Writing"));
            await SendAsync([0x03, (byte)targetSlot, (byte)startPage, .. chunk], cancellationToken);
        }

        progress?.Report(new TransferProgress(data.Length, data.Length, "Writing"));
        await SelectSlotAsync(targetSlot, cancellationToken);
    }

    public Task SelectSlotAsync(int slot, CancellationToken cancellationToken = default) =>
        SendAsync([0xFF, (byte)slot], cancellationToken);

    public async Task<byte[]> ClearSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        // The legacy firmware has no clear command; write a blank tag instead.
        var blank = TagDump.Blank();
        await WriteTagAsync(blank, slot, null, cancellationToken);
        return blank.Uid;
    }

    public async Task RenameAsync(string name, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > MaxNameBytes)
        {
            throw new BleException($"Name is too long ({bytes.Length} bytes, max {MaxNameBytes}).");
        }

        var characteristic = _nameCharacteristic ?? throw new BleException("This puck can't be renamed.");
        _name = name;
        await characteristic.WriteAsync(bytes, true, cancellationToken);
        await DisconnectAsync();
    }

    public async Task EnableUartAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync([0xFE], cancellationToken);
        await DisconnectAsync();
    }

    private IBleCharacteristic RequireCommand() => _command ?? throw new BleException("The puck isn't connected.");
}
