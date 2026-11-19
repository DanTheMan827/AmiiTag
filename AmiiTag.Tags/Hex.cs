namespace AmiiTag.Tags;

/// <summary>
/// Small hex helpers that behave the same on every platform (including the browser).
/// </summary>
public static class Hex
{
    /// <summary>
    /// Converts bytes to a lowercase hex string.
    /// </summary>
    public static string ToHex(ReadOnlySpan<byte> bytes, bool prefix = false)
    {
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return prefix ? "0x" + hex : hex;
    }

    /// <summary>
    /// Parses a hex string, ignoring an optional 0x prefix, whitespace, dashes and colons.
    /// </summary>
    public static byte[] FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var cleaned = new System.Text.StringBuilder(hex.Length);
        var span = hex.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        foreach (var c in span)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == ':')
            {
                continue;
            }

            cleaned.Append(c);
        }

        return Convert.FromHexString(cleaned.ToString());
    }

    /// <summary>
    /// Formats bytes as space separated hex, useful for logging protocol traffic.
    /// </summary>
    public static string ToSpacedHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
