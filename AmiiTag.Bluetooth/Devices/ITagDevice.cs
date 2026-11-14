using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

public enum TagDeviceKind
{
    Unknown,

    /// <summary>A puck.js (or compatible) running the DTM puck firmware, on the Nordic UART service.</summary>
    Puck,

    /// <summary>The older puck firmware with its own 7829xxxx service.</summary>
    LegacyPuck,

    AmiiboLink,
    AmiLoop,
}

public readonly record struct TransferProgress(int Current, int Total, string? Message = null)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Current / Total, 0, 1);
}

/// <summary>
/// A connected Bluetooth tag emulator. Optional features are exposed through the other interfaces in this namespace;
/// the UI checks for them with <c>is</c>.
/// </summary>
public interface ITagDevice : IAsyncDisposable
{
    string Name { get; }

    TagDeviceKind Kind { get; }

    /// <summary>
    /// Firmware or version text the device reported, if any.
    /// </summary>
    string? Firmware { get; }

    BleDeviceInfo Info { get; }

    bool IsConnected { get; }

    event EventHandler? Disconnected;

    /// <summary>
    /// Writes a dump. Devices with slots write to <paramref name="slot"/> (or the active slot when null).
    /// </summary>
    Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}

public sealed record SlotSummary(int Current, int Total);

/// <summary>
/// A slot as reported by the device. <see cref="Dump"/> only contains the identifying parts of the tag
/// (UID, ID block, salt and the nickname area), enough to show a name and image.
/// </summary>
public sealed record SlotInfo(int Slot, TagDump Dump);

public interface ISlotTagDevice : ITagDevice
{
    Task<SlotSummary> GetSlotSummaryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SlotInfo>> GetSlotsAsync(IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<TagDump> ReadSlotAsync(int slot, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes <paramref name="slot"/> the active tag (restarts NFC emulation).
    /// </summary>
    Task SelectSlotAsync(int slot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the slot with a blank NTAG215. Returns the new UID.
    /// </summary>
    Task<byte[]> ClearSlotAsync(int slot, CancellationToken cancellationToken = default);
}

public interface IRenamableTagDevice : ITagDevice
{
    int MaxNameBytes { get; }

    /// <summary>
    /// Renames the device. Most devices disconnect afterwards.
    /// </summary>
    Task RenameAsync(string name, CancellationToken cancellationToken = default);
}

public interface IUartTagDevice : ITagDevice
{
    /// <summary>
    /// Drops back to the Espruino console over BLE UART and disconnects.
    /// </summary>
    Task EnableUartAsync(CancellationToken cancellationToken = default);
}

public interface IAmiitoolTagDevice : ISlotTagDevice
{
    bool AmiitoolEnabled { get; }

    bool AmiitoolKeysLoaded { get; }

    Task<bool> UploadKeysAsync(byte[] keys, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-encrypts the slot for a new random UID on the device. Returns the new UID.
    /// </summary>
    Task<byte[]> RandomizeUidAsync(int slot, CancellationToken cancellationToken = default);
}

/// <summary>
/// A simple action a device supports (mode switches, reset, info...).
/// </summary>
public sealed record TagDeviceCommand(string Id, string Title, bool IsDestructive = false);

public interface ICommandTagDevice : ITagDevice
{
    IReadOnlyList<TagDeviceCommand> Commands { get; }

    /// <summary>
    /// Runs a command. Returns text to show the user, if the device replied with something meaningful.
    /// </summary>
    Task<string?> RunCommandAsync(TagDeviceCommand command, CancellationToken cancellationToken = default);
}
