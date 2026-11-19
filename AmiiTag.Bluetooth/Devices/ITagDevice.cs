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

    /// <summary>A Pixl.js (solosky/pixl.js) managed through its BLE file system protocol.</summary>
    Pixl,

    /// <summary>A Flask: Espruino running the Flask app, driven through its JavaScript console.</summary>
    Flask,

    /// <summary>The Flask's sibling, the Slide (same protocol, fewer commands).</summary>
    Slide,
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

    /// <summary>Refresh one slot without enumerating every other slot.</summary>
    async Task<SlotInfo> GetSlotInformationAsync(int slot, CancellationToken cancellationToken = default) =>
        new(slot, await ReadSlotAsync(slot, cancellationToken: cancellationToken));

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

/// <summary>
/// A tag stored on a device that keeps a list of named tags (a Flask). <see cref="DisplayName"/>, the amiibo
/// <see cref="AmiiboTail"/> (8 hex digits, when the name carries one) and <see cref="SetKey"/> are parsed from
/// <see cref="Name"/>, the name the device stores it under.
/// </summary>
public sealed record StoredTag(int Index, string Name, string DisplayName, string? AmiiboTail, string? SetKey)
{
    /// <summary>
    /// The device is emulating it in virtual tag mode.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// The device's own ID for the tag, when it addresses tags by ID rather than by name (next-gen Flask firmware).
    /// </summary>
    public int? Id { get; init; }

    /// <summary>
    /// What kind of tag the device stores it as ("215" for NTAG215, "213", "PLUS", "1k", "2k"), when it says.
    /// </summary>
    public string? TagType { get; init; }

    /// <summary>
    /// The folder it's in (<see cref="TagFolder.Id"/>), or 0 for none.
    /// </summary>
    public int FolderId { get; init; }

    /// <summary>
    /// The image chosen for it on the device, if any.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    /// Whether it's stored as an NTAG215 (an amiibo-sized dump the app can open).
    /// </summary>
    public bool IsNtag215 => TagType is null or "215";
}

/// <summary>
/// A folder on a device that groups its stored tags (next-gen Flask firmware).
/// </summary>
public sealed record TagFolder(int Id, string Name);

/// <summary>
/// A device holding a list of named tags rather than fixed slots: tags are added and removed, one is selected for
/// emulation, and the list can be reordered.
/// </summary>
public interface ITagListDevice : ITagDevice
{
    /// <summary>
    /// Raised when the device changes its tags itself (a button press selects another, a tag is deleted on the device,
    /// a game renames one).
    /// </summary>
    event EventHandler? TagsChanged;

    Task<IReadOnlyList<StoredTag>> GetTagsAsync(CancellationToken cancellationToken = default);

    Task<StoredTag?> GetCurrentTagAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the tag the one emulated. Returns the selected tag as the device reports it.
    /// </summary>
    Task<StoredTag?> SelectTagAsync(StoredTag tag, CancellationToken cancellationToken = default);

    Task<TagDump> DownloadTagAsync(StoredTag tag, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds tags, each named after <c>DisplayName</c> (shortened and de-duplicated as the device needs).
    /// </summary>
    Task UploadTagsAsync(IReadOnlyList<(string DisplayName, TagDump Dump)> tags, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<StoredTag?> RenameTagAsync(StoredTag tag, string displayName, CancellationToken cancellationToken = default);

    Task DeleteTagAsync(StoredTag tag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <see cref="MoveTagAsync"/> works: the next-gen Flask firmware can't reorder tags.
    /// </summary>
    bool CanReorderTags => true;

    Task MoveTagAsync(StoredTag tag, int newIndex, CancellationToken cancellationToken = default);

    Task CreateBlankTagAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A device that stores amiibo keys so it can generate or re-encrypt tags itself (a Flask).
/// </summary>
public interface IKeyStoreTagDevice : ITagDevice
{
    /// <summary>
    /// Whether keys can be uploaded to this device (its firmware build supports it).
    /// </summary>
    bool CanUploadKeys { get; }

    /// <summary>
    /// Whether the device reported having keys, when it says.
    /// </summary>
    bool HasKeys { get; }

    /// <summary>
    /// Uploads key_retail.bin.
    /// </summary>
    Task UploadKeysAsync(byte[] keys, CancellationToken cancellationToken = default);
}

public interface IRenamableTagDevice : ITagDevice
{
    int MaxNameBytes { get; }

    /// <summary>
    /// Renames the device. Most devices disconnect afterwards.
    /// </summary>
    Task RenameAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// A device that stores amiibo as files (Pixl.js) instead of slots.
/// </summary>
public interface IFileTagDevice : ITagDevice
{
    /// <summary>
    /// Where amiibo uploaded from elsewhere in the app go.
    /// </summary>
    string DefaultFolder { get; }

    /// <summary>
    /// Uploads an amiibo as a .bin file into <paramref name="folder"/> (or <see cref="DefaultFolder"/>).
    /// </summary>
    Task UploadTagAsync(string fileName, TagDump dump, string? folder = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default);
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

    bool CanUploadKeys => AmiitoolEnabled;

    bool CanRandomizeUid => AmiitoolEnabled && AmiitoolKeysLoaded;

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
