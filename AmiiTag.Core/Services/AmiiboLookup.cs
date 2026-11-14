using AmiiTag.AmiiboData;
using AmiiTag.Core.Interfaces;
using AmiiTag.Tags;

namespace AmiiTag.Core.Services;

/// <summary>
/// Describes tags using the amiibo database. Port of the Swift <c>TagDump</c> display extensions.
/// </summary>
public sealed class AmiiboLookup
{
    private readonly IDatabaseContainer _database;
    private readonly KeyStore _keys;

    public AmiiboLookup(IDatabaseContainer database, KeyStore keys)
    {
        _database = database;
        _keys = keys;
    }

    public AmiiboDatabase Database => _database.Database;

    /// <summary>
    /// Validates an image with whatever keys are loaded (see <see cref="AmiiboValidator"/>).
    /// </summary>
    public AmiiboValidation Validate(IAmiiboImage dump) => AmiiboValidator.Validate(dump, _keys.Keys);

    /// <summary>
    /// The figure for an image, only if the image is laid out like an amiibo (a blank tag's zero bytes would
    /// otherwise look like amiibo series 00, Super Smash Bros.).
    /// </summary>
    public AmiiboFigure? GetFigure(IAmiiboImage dump) =>
        Validate(dump).IsAmiibo && _database.Database.Figures.TryGetValue(dump.AmiiboIdHex, out var figure) ? figure : null;

    public AmiiboFigure? GetFigure(string id) =>
        _database.Database.Figures.TryGetValue(id.Trim().ToLowerInvariant().Replace("0x", string.Empty), out var figure) ? figure : null;

    public string GetDisplayName(IAmiiboImage dump)
    {
        var validation = Validate(dump);
        if (!validation.IsAmiibo)
        {
            return validation.Summary;
        }

        var nickname = dump.GetNickname(_keys.DecryptionKeys);
        if (!string.IsNullOrEmpty(nickname))
        {
            return nickname;
        }

        if (GetFigure(dump) is { } figure && !string.IsNullOrEmpty(figure.Name))
        {
            return figure.Name;
        }

        return $"0x{dump.AmiiboIdHex}";
    }

    public string GetSeriesName(IAmiiboImage dump) => !Validate(dump).IsAmiibo
        ? "None"
        : GetFigure(dump)?.Series ?? GetDictionaryValue(_database.Database.AmiiboSeries, dump.AmiiboSeriesHex) ?? "Unknown";

    public string GetTypeName(IAmiiboImage dump) => !Validate(dump).IsAmiibo
        ? "None"
        : GetFigure(dump)?.FigureType ?? GetDictionaryValue(_database.Database.Types, dump.TypeHex) ?? "Unknown";

    // ------------------------------------------------------------------ Slot summaries

    /// <summary>
    /// Whether a puck/emulator slot summary holds an amiibo. Summaries only carry part of a tag (UID, header, settings,
    /// ID and salt: no CC, lock bytes, signatures or app data), so they can't be validated like a tag image; the amiibo
    /// header magic and an amiibo ID are what tell them apart from empty slots. Opening a slot reads the whole tag,
    /// which is validated properly.
    /// </summary>
    public static bool IsAmiiboSummary(TagDump summary) => summary.Span[0x10] == 0xA5 && summary.HasAmiiboId;

    public AmiiboFigure? GetSlotFigure(TagDump summary) =>
        IsAmiiboSummary(summary) && _database.Database.Figures.TryGetValue(summary.AmiiboIdHex, out var figure) ? figure : null;

    public string GetSlotDisplayName(TagDump summary)
    {
        if (!IsAmiiboSummary(summary))
        {
            return "Empty slot";
        }

        // The settings (with the nickname), UID, write counter and salt are all in the summary, so this decrypts.
        var nickname = summary.GetNickname(_keys.DecryptionKeys);
        if (!string.IsNullOrEmpty(nickname))
        {
            return nickname;
        }

        return GetSlotFigure(summary)?.Name is { Length: > 0 } name ? name : $"0x{summary.AmiiboIdHex}";
    }

    private static string? GetDictionaryValue(IReadOnlyDictionary<string, string>? dictionary, string key) =>
        dictionary != null && dictionary.TryGetValue(key, out var value) ? value : null;
}
