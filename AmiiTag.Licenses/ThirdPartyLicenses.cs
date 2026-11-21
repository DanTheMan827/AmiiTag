namespace AmiiTag.Licenses;

/// <summary>
/// The licenses of the third-party software AmiiTag ships. Each is a text file in licenses/ named for the project
/// (e.g. "QRCoder.txt", "dotnet maui.txt"), starting with the project's name, URL, license and what it's used
/// for, then the license text. To add one, drop its file into that folder.
/// </summary>
public static class ThirdPartyLicenses
{
    private const string Prefix = "licenses/";
    private const string Extension = ".txt";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Licenses = new(Load);

    /// <summary>
    /// Every license, keyed by its file name without .txt (lookups ignore case); the text has \n line endings and is
    /// trimmed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetAll() => Licenses.Value;

    private static IReadOnlyDictionary<string, string> Load()
    {
        var assembly = typeof(ThirdPartyLicenses).Assembly;
        var licenses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal) || !resource.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream == null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            licenses[resource[Prefix.Length..^Extension.Length]] = reader.ReadToEnd().Replace("\r\n", "\n").Trim();
        }

        return licenses;
    }
}
