using System.Globalization;
using System.Text.Json;
using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// How full a next-gen Flask's flash is, as it reports with <c>screen.getVersion()</c> and after changes. Tags are
/// stored in pages shared by tags of a size, so freeing a tag doesn't always free a page (<see cref="EffectiveFreePages"/>
/// counts the reusable space in part-used pages too).
/// </summary>
public sealed record FlaskMemoryInfo(int FreePages, int TotalPages, double? EffectiveFreePages, double? EffectiveUsedPercent)
{
    /// <summary>
    /// How much is used, 0 to 1.
    /// </summary>
    public double UsedFraction => EffectiveUsedPercent is { } percent
        ? Math.Clamp(percent / 100, 0, 1)
        : TotalPages <= 0 ? 0 : Math.Clamp((double)(TotalPages - FreePages) / TotalPages, 0, 1);
}

/// <summary>
/// A Flask or Slide on the next-gen firmware (script version 10 and later). It isn't an Espruino console: it accepts
/// the same <c>\x10Bluetooth.println(JSON.stringify(...))</c> framing (<see cref="EspruinoRepl"/>), but only for a fixed
/// set of commands, and answers anything else with <c>ERR: unknown command</c>. Tags are addressed by ID, may be other
/// NFC tag types than NTAG215, can be put in folders and given an image, and the device reports its flash use.
/// The commands are the ones flask.run's next-gen interface sends, character for character, since the firmware matches
/// them as text.
/// </summary>
public sealed class FlaskNextGenDevice : TagDeviceBase, ITagListDevice, ICommandTagDevice, IKeyStoreTagDevice
{
    /// <summary>
    /// The first script version on the next-gen firmware.
    /// </summary>
    public const double MinimumVersion = 10;

    // flask.run's limits: a whole name of 46 characters, of which the display name and tail take at most 36.
    private const int MaxNameLength = 46;
    private const int MaxBaseNameLength = 36;

    // flask.run writes commands 200 bytes at a time once it has seen next-gen firmware.
    private const int NextGenChunkSize = 200;

    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(15);

    private static readonly TagDeviceCommand Battery = new("battery", "Battery Level");
    private static readonly TagDeviceCommand Storage = new("storage", "Storage Used");
    private static readonly TagDeviceCommand DeleteAll = new("delete-all", "Delete All Tags", IsDestructive: true);

    private readonly TagDeviceKind _kind;
    private EspruinoRepl? _repl;

    /// <param name="kind"><see cref="TagDeviceKind.Flask"/> or <see cref="TagDeviceKind.Slide"/>.</param>
    public FlaskNextGenDevice(IBleConnection connection, TagDeviceKind kind) : base(connection)
    {
        if (kind is not (TagDeviceKind.Flask or TagDeviceKind.Slide))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _kind = kind;
    }

    public override TagDeviceKind Kind => _kind;

    /// <summary>
    /// The script version (<c>screen.getVersion().v</c>), 10 or later.
    /// </summary>
    public string? ScriptVersion { get; private set; }

    /// <summary>
    /// The firmware build (<c>process.version</c>): an Espruino version, or "FO1.0" on firmware that takes keys.
    /// </summary>
    public string? FirmwareBuild { get; private set; }

    /// <summary>
    /// The flash use the device last reported.
    /// </summary>
    public FlaskMemoryInfo? Memory { get; private set; }

    /// <summary>
    /// The folders, as of the last <see cref="GetTagsAsync"/>.
    /// </summary>
    public IReadOnlyList<TagFolder> Folders { get; private set; } = [];

    public bool HasKeys { get; private set; }

    /// <summary>
    /// flask.run only sends keys (<c>key.add</c>) to the FO1.0 build.
    /// </summary>
    public bool CanUploadKeys => FirmwareBuild == "FO1.0";

    public bool CanReorderTags => false;

    public event EventHandler? TagsChanged;

    public IReadOnlyList<TagDeviceCommand> Commands => [Battery, Storage, DeleteAll];

    private EspruinoRepl Repl => _repl ?? throw new BleException("The device isn't set up.");

    public override async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        _repl?.Dispose();
        _repl = await EspruinoRepl.OpenAsync(Connection, cancellationToken);
        if (_repl == null)
        {
            return false;
        }

        _repl.EventReceived += OnDeviceEvent;
        try
        {
            if (!IsNextGenVersion(await _repl.EvaluateAsync("screen.getVersion()", null, cancellationToken), out var version))
            {
                return false;
            }

            ScriptVersion = version.TryGetProperty("v", out var v) ? v.ToString() : null;
            HasKeys = version.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.True;
            UpdateMemory(version);

            // Larger writes, as flask.run does from here on; never more than the connection takes in one write.
            var mtu = 0;
            try
            {
                mtu = await Connection.RequestMtuAsync(247, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            _repl.ChunkSize = mtu > 3 ? Math.Min(NextGenChunkSize, mtu - 3) : NextGenChunkSize;

            try
            {
                FirmwareBuild = (await _repl.EvaluateAsync("process.version", null, cancellationToken))?.ToString();
            }
            catch (DeviceCommandException)
            {
                // Only key uploads depend on it.
            }
            Firmware = string.Join(" · ", new[] { ScriptVersion is { } script ? $"v{script}" : null, FirmwareBuild }.Where(part => part != null));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or DeviceCommandException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a <c>screen.getVersion()</c> reply comes from next-gen firmware (<c>v</c> of 10 or more).
    /// </summary>
    public static bool IsNextGenVersion(JsonElement? reply, out JsonElement version)
    {
        version = default;
        if (reply is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("v", out var v))
        {
            return false;
        }

        var number = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
        version = value;
        return number >= MinimumVersion;
    }

    /// <summary>
    /// Whether a <c>screen.getVersion()</c> reply comes from a Slide (<c>d</c> is "1").
    /// </summary>
    public static bool IsSlide(JsonElement version) => version.TryGetProperty("d", out var d) && d.ToString() == "1";

    public async Task<IReadOnlyList<StoredTag>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await Repl.EvaluateAsync("tag.getRichList()", TransferTimeout, cancellationToken);
        var (tags, folders) = ParseRichList(reply);
        Folders = folders;
        return tags;
    }

    /// <summary>
    /// <c>tag.getRichList()</c>'s reply: <c>{tags, folders}</c>, or just the tags array.
    /// </summary>
    public static (IReadOnlyList<StoredTag> Tags, IReadOnlyList<TagFolder> Folders) ParseRichList(JsonElement? reply)
    {
        JsonElement tagArray;
        var folders = new List<TagFolder>();
        if (reply is { ValueKind: JsonValueKind.Array } array)
        {
            tagArray = array;
        }
        else if (reply is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            tagArray = t;
            if (value.TryGetProperty("folders", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                foreach (var folder in f.EnumerateArray())
                {
                    if (folder.ValueKind == JsonValueKind.Object && TryGetInt(folder, "id", out var id))
                    {
                        folders.Add(new TagFolder(id, GetString(folder, "name") ?? $"Folder {id}"));
                    }
                }
            }
        }
        else
        {
            throw new BleException("The device didn't send its tag list.");
        }

        var tags = new List<StoredTag>();
        foreach (var item in tagArray.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                tags.Add(ToStoredTag(tags.Count, item));
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                tags.Add(FlaskDevice.ToStoredTag(tags.Count, item.GetString() ?? string.Empty) with { Id = tags.Count });
            }
        }

        return (tags, folders);
    }

    /// <summary>
    /// One <c>getRichList</c> entry: <c>{id, index, name, type, tagId, meta}</c>, where <c>meta</c> is
    /// <c>folder,image,0,flag</c> and <c>tagId</c> the 16 hex digit amiibo ID (or another tag's ID).
    /// </summary>
    public static StoredTag ToStoredTag(int position, JsonElement item)
    {
        var name = GetString(item, "name") ?? string.Empty;
        var tag = FlaskDevice.ToStoredTag(position, name);
        var index = TryGetInt(item, "index", out var i) ? i : position;
        var id = TryGetInt(item, "id", out var parsedId) ? parsedId : index;
        var type = GetString(item, "type");

        // The amiibo ID itself is more reliable than the tail in the name (blank and renamed tags).
        var tail = tag.AmiiboTail;
        if (GetString(item, "tagId") is { Length: 16 } tagId && ulong.TryParse(tagId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var amiiboId))
        {
            tail = amiiboId == 0 || type is not (null or "215" or "213" or "PLUS") ? null : tagId[8..].ToLowerInvariant();
        }

        var meta = (GetString(item, "meta") ?? string.Empty).Split(',');
        var folder = meta.Length > 0 && int.TryParse(meta[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var image = meta.Length > 1 && meta[1] is { Length: > 0 } m && m != "0" ? m : null;
        return tag with { AmiiboTail = tail, Id = id, TagType = type, FolderId = folder, Image = image };
    }

    public async Task<StoredTag?> GetCurrentTagAsync(CancellationToken cancellationToken = default) =>
        ParseCurrent(await Repl.EvaluateAsync("tag.get()", null, cancellationToken));

    public async Task<StoredTag?> SelectTagAsync(StoredTag tag, CancellationToken cancellationToken = default)
    {
        var reply = await Repl.EvaluateAsync($"tag.setById({IdOf(tag)})", null, cancellationToken);
        return ParseCurrent(reply) ?? await GetCurrentTagAsync(cancellationToken);
    }

    public async Task<TagDump> DownloadTagAsync(StoredTag tag, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var bytes = await DownloadDataAsync(tag, progress, cancellationToken);
        return TagDump.TryCreate(bytes, out var dump) && dump != null
            ? dump
            : throw new BleException($"{tag.DisplayName} isn't an NTAG215 dump ({bytes.Length} bytes). Save it as a file instead.");
    }

    /// <summary>
    /// The tag's data as stored, whatever its type (NTAG215 dumps, MIFARE 1K/2K images...).
    /// </summary>
    public async Task<byte[]> DownloadDataAsync(StoredTag tag, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new TransferProgress(0, 1, $"Reading {tag.DisplayName}"));
        var reply = await Repl.EvaluateAsync($"tag.download({EspruinoRepl.Quote(tag.Name)})", TransferTimeout, cancellationToken);
        if (reply is not { ValueKind: JsonValueKind.Array } values)
        {
            throw new BleException($"Couldn't read {tag.DisplayName}.");
        }

        var bytes = values.EnumerateArray().Select(value => (byte)value.GetInt32()).ToArray();
        progress?.Report(new TransferProgress(1, 1));
        return bytes;
    }

    public Task UploadTagsAsync(IReadOnlyList<(string DisplayName, TagDump Dump)> tags, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        UploadAsync(tags.Select(tag => (tag.DisplayName, tag.Dump.ToArray(TagDump.DataLength))).ToList(), 0, progress, cancellationToken);

    /// <summary>
    /// Whether the firmware takes data of this size: flask.run allows up to 180 bytes (NTAG213 and smaller), 532-574
    /// (NTAG215 with or without its signature), and 1024 or 2048 (MIFARE Classic 1K, 2K).
    /// </summary>
    public static bool IsSupportedSize(int length) => length is > 0 and <= 180 or >= 532 and <= 574 or 1024 or 2048;

    /// <summary>
    /// Adds tags into a folder (0 for none), each named after <c>DisplayName</c> as flask.run names them:
    /// <c>display name|amiibo tail in base 36|0</c>, de-duplicated with a number.
    /// </summary>
    public async Task UploadAsync(IReadOnlyList<(string DisplayName, byte[] Data)> tags, int folderId = 0, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (tags.Count == 0)
        {
            return;
        }

        foreach (var (displayName, data) in tags)
        {
            if (!IsSupportedSize(data.Length))
            {
                throw new BleException($"{displayName} isn't a tag size the device takes ({data.Length} bytes).");
            }
        }

        var existing = (await GetTagsAsync(cancellationToken)).Select(tag => tag.Name).ToList();
        for (var i = 0; i < tags.Count; i++)
        {
            var (displayName, data) = tags[i];
            var name = FlaskDevice.BuildName(data, displayName, existing, MaxNameLength, MaxBaseNameLength);
            existing.Add(name);
            progress?.Report(new TransferProgress(i, tags.Count, $"Uploading {displayName}"));
            var reply = await Repl.EvaluateAsync(
                $"tag.uploadWithMeta({EspruinoRepl.Quote(name)},{i + 1},{tags.Count},\"{Convert.ToBase64String(data)}\",{folderId},0,0)",
                TransferTimeout, cancellationToken);
            UpdateMemory(reply);
        }

        progress?.Report(new TransferProgress(tags.Count, tags.Count));
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Writes a dump as a new tag on the device, named "amiibo". The app names uploads after the figure with
    /// <see cref="UploadTagsAsync"/>.
    /// </summary>
    public override Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        UploadTagsAsync([("amiibo", dump)], progress, cancellationToken);

    public async Task<StoredTag?> RenameTagAsync(StoredTag tag, string displayName, CancellationToken cancellationToken = default)
    {
        var suffix = tag.Name.Contains('|') ? tag.Name[tag.Name.IndexOf('|')..] : string.Empty;
        var name = FlaskDevice.Fit(FlaskDevice.SanitizeDisplayName(displayName) + suffix, MaxNameLength);
        await Repl.EvaluateAsync($"tag.rename({EspruinoRepl.Quote(tag.Name)}, {EspruinoRepl.Quote(name)})", null, cancellationToken);
        return await GetCurrentTagAsync(cancellationToken);
    }

    public Task DeleteTagAsync(StoredTag tag, CancellationToken cancellationToken = default) => DeleteTagsAsync([tag], cancellationToken);

    /// <summary>
    /// Deletes several tags with one command.
    /// </summary>
    public async Task DeleteTagsAsync(IReadOnlyList<StoredTag> tags, CancellationToken cancellationToken = default)
    {
        if (tags.Count == 0)
        {
            return;
        }

        UpdateMemory(await Repl.EvaluateAsync($"tag.deleteById({string.Join(",", tags.Select(IdOf))})", TransferTimeout, cancellationToken));
    }

    /// <summary>
    /// Not supported: the next-gen firmware keeps tags in its own order (see <see cref="CanReorderTags"/>).
    /// </summary>
    public Task MoveTagAsync(StoredTag tag, int newIndex, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This firmware can't reorder tags.");

    public async Task CreateBlankTagAsync(CancellationToken cancellationToken = default)
    {
        UpdateMemory(await Repl.EvaluateAsync("tag.createBlank()", null, cancellationToken));
    }

    /// <summary>
    /// Sets the image the device shows for a tag (an image ID from flask.run's catalog, such as "folder").
    /// </summary>
    public async Task SetTagImageAsync(StoredTag tag, string image, CancellationToken cancellationToken = default)
    {
        UpdateMemory(await Repl.EvaluateAsync($"tag.setImage({IdOf(tag)}, {EspruinoRepl.Quote(image)})", null, cancellationToken));
    }

    public async Task CreateFolderAsync(string name, CancellationToken cancellationToken = default)
    {
        UpdateMemory(await Repl.EvaluateAsync($"tag.createFolder({EspruinoRepl.Quote(FlaskDevice.SanitizeDisplayName(name))})", null, cancellationToken));
    }

    /// <summary>
    /// Deletes a folder. Delete or move its tags first: flask.run only offers this for empty folders.
    /// </summary>
    public async Task DeleteFolderAsync(TagFolder folder, CancellationToken cancellationToken = default)
    {
        UpdateMemory(await Repl.EvaluateAsync($"folder.remove({folder.Id})", null, cancellationToken));
    }

    /// <summary>
    /// Reorders folders: puts the folder after <paramref name="after"/>, or first when it's null.
    /// </summary>
    public async Task MoveFolderAsync(TagFolder folder, TagFolder? after, CancellationToken cancellationToken = default)
    {
        await Repl.EvaluateAsync($"tag.move({folder.Id}, {after?.Id ?? -1})", null, cancellationToken);
    }

    public async Task UploadKeysAsync(byte[] keys, CancellationToken cancellationToken = default)
    {
        UpdateMemory(await Repl.EvaluateAsync($"key.add(\"{Convert.ToBase64String(keys)}\")", null, cancellationToken));
        HasKeys = true;
    }

    /// <summary>
    /// Asks for the flash use again.
    /// </summary>
    public async Task<FlaskMemoryInfo?> RefreshMemoryAsync(CancellationToken cancellationToken = default)
    {
        UpdateMemory(await Repl.EvaluateAsync("screen.getVersion()", null, cancellationToken));
        return Memory;
    }

    public async Task<string?> RunCommandAsync(TagDeviceCommand command, CancellationToken cancellationToken = default)
    {
        switch (command.Id)
        {
            case "battery":
                return $"{(await Repl.EvaluateAsync("Puck.getBatteryPercentage()", null, cancellationToken))?.ToString() ?? "?"}%";

            case "storage":
                return await RefreshMemoryAsync(cancellationToken) is { } memory
                    ? $"{memory.UsedFraction:P0} used ({memory.FreePages} of {memory.TotalPages} pages free)."
                    : "The device didn't say.";

            case "delete-all":
                // The firmware knows this command by its text; flask.run sends exactly this.
                UpdateMemory(await Repl.EvaluateAsync("function deleteAll()", TimeSpan.FromSeconds(60), cancellationToken));
                TagsChanged?.Invoke(this, EventArgs.Empty);
                return "All tags deleted.";

            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static int IdOf(StoredTag tag) => tag.Id ?? tag.Index;

    private void UpdateMemory(JsonElement? reply)
    {
        if (reply is { ValueKind: JsonValueKind.Object } value && TryGetInt(value, "free_pages", out var free) && TryGetInt(value, "total_pages", out var total))
        {
            Memory = new FlaskMemoryInfo(free, total, TryGetDouble(value, "effective_free_pages"), TryGetDouble(value, "effective_used_percent"));
        }
    }

    private void OnDeviceEvent(object? sender, JsonElement value)
    {
        if (value.TryGetProperty("event", out var name) && name.GetString() is "button" or "delete" or "refresh")
        {
            TagsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static StoredTag? ParseCurrent(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } current || !current.TryGetProperty("name", out _))
        {
            return null;
        }

        var index = TryGetInt(current, "index", out var i) ? i : -1;
        var tag = FlaskDevice.ToStoredTag(index, GetString(current, "name") ?? string.Empty);
        return tag with
        {
            Id = TryGetInt(current, "id", out var id) ? id : index,
            IsVirtual = current.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.True,
        };
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static double? TryGetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number ? property.GetDouble() : null;

    public override void Detach()
    {
        if (_repl != null)
        {
            _repl.EventReceived -= OnDeviceEvent;
            _repl.Dispose();
            _repl = null;
        }

        base.Detach();
    }

    public override async Task DisconnectAsync()
    {
        _repl?.Dispose();
        await base.DisconnectAsync();
    }
}
