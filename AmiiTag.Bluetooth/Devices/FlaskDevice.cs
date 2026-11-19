using System.Globalization;
using System.Text;
using System.Text.Json;
using AmiiTag.Tags;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// A Flask (or its sibling, the Slide): Espruino running the Flask app, which stores amiibo as named files and is driven
/// through JavaScript calls on its console (<see cref="EspruinoRepl"/>): <c>tag.getList()</c>, <c>tag.download()</c>,
/// <c>tag.upload()</c>... Each tag is a 540 byte NTAG215 dump named <c>display name|amiibo tail in base 36|set</c>.
/// </summary>
/// <remarks>
/// The next-gen firmware (script version 10 and later) has its own driver, <see cref="FlaskNextGenDevice"/>.
/// </remarks>
public sealed class FlaskDevice : TagDeviceBase, ITagListDevice, ICommandTagDevice, IKeyStoreTagDevice
{
    private const int MaxNameLength = 27;

    // The display name and tail together get at most this many characters, leaving room for the set and numbering.
    private const int MaxBaseNameLength = 22;

    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(15);

    private static readonly TagDeviceCommand Battery = new("battery", "Battery Level");
    private static readonly TagDeviceCommand Compact = new("compact", "Compact Storage");
    private static readonly TagDeviceCommand SortByName = new("sort-name", "Sort Tags A–Z");
    private static readonly TagDeviceCommand SortByAdded = new("sort-added", "Sort Tags by Date Added");
    private static readonly TagDeviceCommand Face1 = new("face-0", "Screen Face 1");
    private static readonly TagDeviceCommand Face2 = new("face-1", "Screen Face 2");
    private static readonly TagDeviceCommand DeleteAll = new("delete-all", "Delete All Tags", IsDestructive: true);

    private readonly TagDeviceKind _kind;
    private EspruinoRepl? _repl;
    private int _renames;

    /// <param name="kind"><see cref="TagDeviceKind.Flask"/> or <see cref="TagDeviceKind.Slide"/>.</param>
    public FlaskDevice(IBleConnection connection, TagDeviceKind kind) : base(connection)
    {
        if (kind is not (TagDeviceKind.Flask or TagDeviceKind.Slide))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        _kind = kind;
    }

    public override TagDeviceKind Kind => _kind;

    /// <summary>
    /// The script version (<c>screen.getVersion().v</c>), e.g. 8.2 for a Flask or 6.5 for a Slide.
    /// </summary>
    public string? ScriptVersion { get; private set; }

    /// <summary>
    /// The Espruino build (<c>process.version</c>).
    /// </summary>
    public string? EspruinoVersion { get; private set; }

    /// <summary>
    /// Flask only: whether amiibo keys are stored on it (<c>.ek</c>), which lets it randomize UIDs.
    /// </summary>
    public bool HasKeys { get; private set; }

    public event EventHandler? TagsChanged;

    public IReadOnlyList<TagDeviceCommand> Commands => Kind == TagDeviceKind.Flask
        ? [Battery, Compact, SortByName, SortByAdded, Face1, Face2, DeleteAll]
        : [Battery, Compact, SortByName, SortByAdded, DeleteAll];

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
            await _repl.ClearInputLineAsync(cancellationToken);

            // The app's own handshake: the tag list (an array when the Flask app runs), then a REPL history reset.
            if (await _repl.EvaluateAsync("typeof tag!==\"undefined\"&&typeof tag.getList===\"function\"?tag.getList():\"ERROR\"", null, cancellationToken)
                is not { ValueKind: JsonValueKind.Array })
            {
                return false;
            }

            await ClearHistoryAsync(cancellationToken);
            if (await _repl.EvaluateAsync("screen.getVersion()", null, cancellationToken) is { ValueKind: JsonValueKind.Object } version)
            {
                ScriptVersion = version.TryGetProperty("v", out var v) ? v.ToString() : null;
                HasKeys = version.TryGetProperty("k", out var k) && k.ValueKind == JsonValueKind.True;
            }

            EspruinoVersion = (await _repl.EvaluateAsync("process.version", null, cancellationToken))?.ToString();
            Firmware = string.Join(" · ", new[] { ScriptVersion is { } script ? $"v{script}" : null, EspruinoVersion is { } espruino ? $"Espruino {espruino}" : null }
                .Where(part => part != null));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or DeviceCommandException)
        {
            // A next-gen Flask rejects these calls: it's FlaskNextGenDevice's.
            return false;
        }
    }

    /// <summary>
    /// The Flask app reads keys from <c>.ek</c>; the official app only writes it on Espruino 2v16.
    /// </summary>
    public bool CanUploadKeys => Kind == TagDeviceKind.Flask && EspruinoVersion == "2v16";

    public async Task UploadKeysAsync(byte[] keys, CancellationToken cancellationToken = default)
    {
        await Repl.EvaluateAsync($"require(\"Storage\").write('.ek',atob({EspruinoRepl.Quote(Convert.ToBase64String(keys))}))", TransferTimeout, cancellationToken);
        HasKeys = true;
    }

    public async Task<IReadOnlyList<StoredTag>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        var list = await Repl.EvaluateAsync("tag.getList()", null, cancellationToken);
        if (list is not { ValueKind: JsonValueKind.Array } names)
        {
            throw new BleException("The Flask app isn't running on the device.");
        }

        return names.EnumerateArray().Select((name, index) => ToStoredTag(index, name.GetString() ?? string.Empty)).ToList();
    }

    public async Task<StoredTag?> GetCurrentTagAsync(CancellationToken cancellationToken = default) =>
        ParseCurrent(await Repl.EvaluateAsync("tag.get()", null, cancellationToken));

    public async Task<StoredTag?> SelectTagAsync(StoredTag tag, CancellationToken cancellationToken = default) =>
        ParseCurrent(await Repl.EvaluateAsync($"tag.setTag({EspruinoRepl.Quote(tag.Name)}),setInput(true)", null, cancellationToken));

    public async Task<TagDump> DownloadTagAsync(StoredTag tag, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new TransferProgress(0, 1, $"Reading {tag.DisplayName}"));
        var reply = await Repl.EvaluateAsync($"tag.download({EspruinoRepl.Quote(tag.Name)})", TransferTimeout, cancellationToken);
        if (reply is not { ValueKind: JsonValueKind.Array } values)
        {
            throw new BleException($"Couldn't read {tag.DisplayName}.");
        }

        var bytes = values.EnumerateArray().Select(value => (byte)value.GetInt32()).ToArray();
        progress?.Report(new TransferProgress(1, 1));
        return TagDump.TryCreate(bytes, out var dump) && dump != null
            ? dump
            : throw new BleException($"{tag.DisplayName} isn't an NTAG215 dump ({bytes.Length} bytes).");
    }

    public async Task UploadTagsAsync(IReadOnlyList<(string DisplayName, TagDump Dump)> tags, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (tags.Count == 0)
        {
            return;
        }

        var existing = (await GetTagsAsync(cancellationToken)).Select(tag => tag.Name).ToList();
        var completed = false;
        try
        {
            for (var i = 0; i < tags.Count; i++)
            {
                var (displayName, dump) = tags[i];
                var name = BuildName(dump, displayName, existing);
                existing.Add(name);
                progress?.Report(new TransferProgress(i, tags.Count, $"Uploading {displayName}"));
                var data = Convert.ToBase64String(dump.ToArray(TagDump.DataLength));
                var reply = await Repl.EvaluateAsync($"tag.upload({EspruinoRepl.Quote(name)},{i + 1},{tags.Count},\"{data}\")", TransferTimeout, cancellationToken);
                if (reply is not { ValueKind: JsonValueKind.True })
                {
                    throw new BleException($"The device didn't accept {displayName}.");
                }
            }

            completed = true;
            progress?.Report(new TransferProgress(tags.Count, tags.Count));
        }
        finally
        {
            if (!completed)
            {
                // Leaves upload mode (it otherwise keeps the buttons disabled until the batch's last tag).
                try
                {
                    await Repl.EvaluateAsync("tag.uploadsComplete()", null, CancellationToken.None);
                }
                catch (Exception ex) when (ex is TimeoutException or BleException)
                {
                }
            }

            await ClearHistoryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Writes a dump as a new tag on the device (a Flask has no slots to overwrite), named "amiibo". The app names
    /// uploads after the figure with <see cref="UploadTagsAsync"/>.
    /// </summary>
    public override Task WriteTagAsync(TagDump dump, int? slot = null, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
        UploadTagsAsync([("amiibo", dump)], progress, cancellationToken);

    public async Task<StoredTag?> RenameTagAsync(StoredTag tag, string displayName, CancellationToken cancellationToken = default)
    {
        var suffix = tag.Name.Contains('|') ? tag.Name[tag.Name.IndexOf('|')..] : string.Empty;
        var name = Fit(SanitizeDisplayName(displayName) + suffix);

        // The firmware re-stores the selected tag under the new name, so it's selected first.
        await Repl.EvaluateAsync($"tag.setTag({EspruinoRepl.Quote(tag.Name)})", null, cancellationToken);
        var current = ParseCurrent(await Repl.EvaluateAsync($"tag.rename({EspruinoRepl.Quote(tag.Name)},{EspruinoRepl.Quote(name)})", null, cancellationToken));

        // Erased records keep using flash until compacted, as the app does every tenth rename.
        if (++_renames % 10 == 0)
        {
            await Repl.EvaluateAsync("require('Storage').compact()", TransferTimeout, cancellationToken);
        }

        await Repl.EvaluateAsync("setInput(true)", null, cancellationToken);
        return current;
    }

    public async Task DeleteTagAsync(StoredTag tag, CancellationToken cancellationToken = default)
    {
        await Repl.EvaluateAsync($"tag.remove({EspruinoRepl.Quote(tag.Name)})", null, cancellationToken);
        await ClearHistoryAsync(cancellationToken);
    }

    public async Task MoveTagAsync(StoredTag tag, int newIndex, CancellationToken cancellationToken = default)
    {
        await Repl.EvaluateAsync($"function(){{updatePosition({EspruinoRepl.Quote(tag.Name)},{newIndex});}}()", null, cancellationToken);
    }

    public async Task CreateBlankTagAsync(CancellationToken cancellationToken = default)
    {
        await Repl.EvaluateAsync("tag.createBlank()", null, cancellationToken);
    }

    public async Task<string?> RunCommandAsync(TagDeviceCommand command, CancellationToken cancellationToken = default)
    {
        switch (command.Id)
        {
            case "battery":
                return $"{(await Repl.EvaluateAsync("Puck.getBatteryPercentage()", null, cancellationToken))?.ToString() ?? "?"}%";

            case "compact":
                await Repl.EvaluateAsync("require('Storage').compact()", TransferTimeout, cancellationToken);
                return "Storage compacted.";

            case "sort-name":
            case "sort-added":
                await SetSortAsync(command.Id == "sort-name", cancellationToken);
                return null;

            case "face-0":
            case "face-1":
                await Repl.EvaluateAsync($"screen.setFace(\"{command.Id[^1]}\")", null, cancellationToken);
                return null;

            case "delete-all":
                await Repl.EvaluateAsync(
                    "function(){global[\"\\xff\"].history=[];var s=require(\"Storage\");tag.tagStorage._storage.list(/^[a-zA-Z0-9]/).forEach(s.erase);" +
                    "s.erase('.sort');tag.tagList=tag.tagStorage.getAllTags();tag.reload();if(tag.tagList.length===0){tag._screen.splash();}" +
                    "tag.curTag=0;require('Storage').compact();return {event:'deleteAllE'};}()",
                    TimeSpan.FromSeconds(60), cancellationToken);
                TagsChanged?.Invoke(this, EventArgs.Empty);
                return "All tags deleted.";

            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    /// <summary>
    /// A–Z or the order tags were added; then the first tag is selected again, which also turns the buttons back on.
    /// </summary>
    private async Task SetSortAsync(bool byName, CancellationToken cancellationToken)
    {
        var value = byName ? 1 : 0;
        await Repl.EvaluateAsync(
            $"function(){{require(\"Storage\").write('.sort',\"{value}\");sortConfig={value};setInput(false);if(tag.tagList&&tag.tagList.length)tag.refreshTaglist();}}()",
            TransferTimeout, cancellationToken);
        var tags = await GetTagsAsync(cancellationToken);
        if (tags.Count > 0)
        {
            await SelectTagAsync(tags[0], cancellationToken);
        }
        else
        {
            await Repl.EvaluateAsync("setInput(true)", null, cancellationToken);
        }

        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    // The device has little RAM, and the console keeps every long command in its history.
    private async Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Repl.EvaluateAsync("global[\"\\xff\"].history=[]", null, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    private void OnDeviceEvent(object? sender, JsonElement value)
    {
        // A button press changed the tag, a tag was deleted on the device, or a game rewrote one and its name changed.
        if (value.TryGetProperty("event", out var name) && name.GetString() is "button" or "delete" or "refresh")
        {
            TagsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private StoredTag? ParseCurrent(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } current)
        {
            return null;
        }

        var index = current.TryGetProperty("index", out var i) && i.TryGetInt32(out var parsed) ? parsed : -1;
        var name = current.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var tag = ToStoredTag(index, name);
        return tag with { IsVirtual = current.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.True };
    }

    /// <summary>
    /// A stored name, <c>display name|tail in base 36|set</c>, split into its parts. Tags without a tail (or one shorter
    /// than 4 digits) are other NFC tags, as the Flask app shows them.
    /// </summary>
    public static StoredTag ToStoredTag(int index, string name)
    {
        var parts = name.Split('|');
        string? tail = null;
        if (parts.Length > 1 && parts[1].Length >= 4 && TryParseBase36(parts[1], out var value) && value <= uint.MaxValue)
        {
            tail = value.ToString("x8", CultureInfo.InvariantCulture);
        }

        return new StoredTag(index, name, parts[0], tail, parts.Length > 2 && parts[2] != "0" ? parts[2] : null);
    }

    /// <summary>
    /// The stored name for an upload, built the way the Flask app builds it so its UI and sets recognise the tag:
    /// <c>display name|amiibo tail (bytes 88-91) in base 36|0</c>, at most 27 characters, numbered if it already exists.
    /// </summary>
    public static string BuildName(TagDump dump, string displayName, IReadOnlyCollection<string> existing) =>
        BuildName(dump.ToArray(TagDump.DataLength), displayName, existing, MaxNameLength, MaxBaseNameLength);

    /// <summary>
    /// <see cref="BuildName(TagDump, string, IReadOnlyCollection{string})"/> for any tag data, with the given limits
    /// (the next-gen firmware allows longer names). Data too short to hold an amiibo ID gets tail 0.
    /// </summary>
    internal static string BuildName(byte[] data, string displayName, IReadOnlyCollection<string> existing, int maxLength, int maxBaseLength)
    {
        var tail = data.Length >= 92 ? ((uint)data[88] << 24) | ((uint)data[89] << 16) | ((uint)data[90] << 8) | data[91] : 0;
        var display = SanitizeDisplayName(displayName);
        var tailPart = "|" + ToBase36(tail);

        if (display.Length + tailPart.Length > maxBaseLength)
        {
            display = display[..Math.Max(1, maxBaseLength - tailPart.Length)];
        }

        var baseName = display + tailPart;
        var name = Fit($"{baseName}|0", maxLength);
        for (var number = 1; existing.Contains(name); number++)
        {
            name = Fit($"{number}. {baseName}|0", maxLength);
        }

        return name;
    }

    /// <summary>
    /// What a display name may contain: accents removed, underscores as spaces, and only letters, digits, spaces and
    /// hyphens (a name is spliced into JavaScript, and "|" separates its parts).
    /// </summary>
    public static string SanitizeDisplayName(string value)
    {
        var plain = new StringBuilder();
        foreach (var c in value.Replace('_', ' ').Normalize(NormalizationForm.FormD))
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or ' ' or '-')
            {
                plain.Append(c);
            }
        }

        var result = plain.ToString().Trim();
        return result.Length == 0 ? "Tag" : result;
    }

    // Shortens the display name so the whole name fits, keeping its "|tail|set" end.
    internal static string Fit(string name, int maxLength = MaxNameLength)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var separator = name.IndexOf('|');
        if (separator < 0)
        {
            return name[..maxLength];
        }

        var end = name[separator..];
        return name[..Math.Max(1, maxLength - end.Length)] + end;
    }

    internal static string ToBase36(uint value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0)
        {
            return "0";
        }

        var text = new StringBuilder();
        while (value > 0)
        {
            text.Insert(0, digits[(int)(value % 36)]);
            value /= 36;
        }

        return text.ToString();
    }

    private static bool TryParseBase36(string text, out ulong value)
    {
        value = 0;
        foreach (var c in text.ToLowerInvariant())
        {
            var digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : -1;
            if (digit < 0 || value > (ulong.MaxValue - (ulong)digit) / 36)
            {
                return false;
            }

            value = value * 36 + (ulong)digit;
        }

        return text.Length > 0;
    }

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
