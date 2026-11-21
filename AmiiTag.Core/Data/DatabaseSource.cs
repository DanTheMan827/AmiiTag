using System.Text.Json;

namespace AmiiTag.Core.Data;

/// <summary>
/// One amiibo database: a name (also the folder its local copy is kept in) and the URL of the site with its
/// sha1sum.json, database/ and images/.
/// </summary>
public sealed record DatabaseSource(string Name, string Url)
{
    /// <summary>
    /// The name, made safe as a folder name.
    /// </summary>
    public string FolderName => string.Join("_", Name.Split(Path.GetInvalidFileNameChars())).Trim();
}

/// <summary>
/// The list of amiibo databases, loaded in order (a later one adds to the earlier ones). It's published as
/// databases.json beside the web app; the apps fetch it from <see cref="ListUrl"/>. The last list fetched is kept for
/// when it can't be, and <see cref="BuiltIn"/> is used when there's none.
/// </summary>
public static class DatabaseSources
{
    public const string FileName = "databases.json";

    public const string ListUrl = "https://dantheman827.github.io/AmiiTag/" + FileName;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// The databases when no list has ever been fetched.
    /// </summary>
    public static IReadOnlyList<DatabaseSource> BuiltIn { get; } =
    [
        new("N3evin.AmiiboAPI", "https://dantheman827.github.io/AmiiboData"),
        new("DanTheMan827.UnofficialAmiiboDB", "https://dantheman827.github.io/UnofficialAmiiboDB"),
    ];

    /// <summary>
    /// The list in a databases.json, or null if it isn't a usable one: a JSON array of { "name", "url" } objects, each
    /// with a name and an absolute http(s) URL (entries that aren't are skipped; a repeated name keeps its first entry).
    /// </summary>
    public static IReadOnlyList<DatabaseSource>? Parse(ReadOnlySpan<byte> json)
    {
        List<DatabaseSource?>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<DatabaseSource?>>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DatabaseSource>();
        foreach (var entry in entries ?? [])
        {
            if (entry is not { Name: { } name, Url: { } url } || string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            {
                continue;
            }

            var source = new DatabaseSource(name.Trim(), uri.AbsoluteUri.TrimEnd('/'));
            if (source.FolderName.Length > 0 && names.Add(source.FolderName))
            {
                list.Add(source);
            }
        }

        return list.Count > 0 ? list : null;
    }

    public static byte[] Serialize(IReadOnlyList<DatabaseSource> sources) => JsonSerializer.SerializeToUtf8Bytes(sources, Options);
}
