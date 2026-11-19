using System.Security.Cryptography;
using System.Text;
using AmiiTag.Tags;

namespace AmiiTag.Tests;

/// <summary>
/// Finds the real keys for tests that need them: key_retail.bin, or unfixed-info.bin and locked-secret.bin together.
/// They're never part of the repository (.gitignore excludes them): put them in the solution folder (or any folder above
/// the test output), or point the AMIITAG_KEY_RETAIL environment variable at key_retail.bin. Without them, those tests
/// are skipped.
/// </summary>
public static class RetailKeys
{
    public const string FileName = "key_retail.bin";
    public const string EnvironmentVariable = "AMIITAG_KEY_RETAIL";

    private static readonly Lazy<(AmiiboKeySet? Keys, string Reason)> Loaded = new(Load);

    public static AmiiboKeySet? Keys => Loaded.Value.Keys;

    public static string SkipReason => Loaded.Value.Reason;

    public static AmiiboKeySet Required => Keys ?? throw new InvalidOperationException(SkipReason);

    private static (AmiiboKeySet?, string) Load()
    {
        var path = Find(FileName, fromEnvironment: true);
        if (path == null)
        {
            var unfixed = Find("unfixed-info.bin", fromEnvironment: false);
            var locked = Find("locked-secret.bin", fromEnvironment: false);
            if (unfixed != null && locked != null)
            {
                try
                {
                    return (AmiiboKeySet.FromParts(File.ReadAllBytes(unfixed), File.ReadAllBytes(locked)), $"{unfixed} + {locked}");
                }
                catch (TagException ex)
                {
                    return (null, $"The split key files don't match the retail keys: {ex.Message}");
                }
            }

            return (null, $"{FileName} (or unfixed-info.bin and locked-secret.bin) not found. Put it in the solution folder or set {EnvironmentVariable} to its path to run the retail key tests.");
        }

        var data = File.ReadAllBytes(path);
        if (!AmiiboKeySet.IsValidKeyFile(data))
        {
            return (null, $"{path} isn't the retail key file (size or hash doesn't match).");
        }

        return (AmiiboKeySet.FromKeyFile(data), path);
    }

    private static string? Find(string fileName, bool fromEnvironment)
    {
        var environmentPath = fromEnvironment ? Environment.GetEnvironmentVariable(EnvironmentVariable) : null;
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
        {
            return environmentPath;
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Lowercase hex SHA-256: expected values for anything encrypted with the retail keys are stored as hashes only.
    /// </summary>
    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// 32 deterministic bytes (SHA-256 of a label), used for keygen salts and app data in fixtures.
    /// </summary>
    public static byte[] Bytes32(string label) => SHA256.HashData(Encoding.UTF8.GetBytes(label));
}

/// <summary>
/// A fact that only runs when the real key_retail.bin is available.
/// </summary>
public sealed class RetailKeysFactAttribute : FactAttribute
{
    public RetailKeysFactAttribute()
    {
        if (RetailKeys.Keys == null)
        {
            Skip = RetailKeys.SkipReason;
        }
    }
}

/// <summary>
/// A fact comparing a hash of retail key output with a recorded value. Skipped without key_retail.bin, or until the
/// hash has been recorded in <see cref="RetailKeyGoldenHashes"/>.
/// </summary>
public sealed class RetailKeysGoldenFactAttribute : FactAttribute
{
    public RetailKeysGoldenFactAttribute(string name)
    {
        if (RetailKeys.Keys == null)
        {
            Skip = RetailKeys.SkipReason;
        }
        else if (string.IsNullOrEmpty(RetailKeyGoldenHashes.Get(name)))
        {
            Skip = $"The {name} hash hasn't been recorded yet. Run RetailKeyTests.PrintGoldenHashes and copy its output into RetailKeyGoldenHashes.";
        }
    }
}
