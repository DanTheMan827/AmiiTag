namespace AmiiTag.Tags;

/// <summary>
/// The physical tag an amiibo image belongs to.
/// </summary>
public enum AmiiboFormat
{
    /// <summary>Original amiibo: NTAG215, 532/540/572 byte dumps, IDs ending in 02.</summary>
    Ntag215,

    /// <summary>"v3" amiibo: NXP NTAG I²C Plus 2K (NT3H2211), 2048 byte images, IDs ending in 03, optional lock-on response.</summary>
    NtagI2cPlus2k,
}

/// <summary>
/// What screens and file handling need from any amiibo image, whatever the tag type.
/// </summary>
public interface IAmiiboImage
{
    AmiiboFormat Format { get; }

    /// <summary>
    /// A copy of the raw image, as saved to a .bin file.
    /// </summary>
    byte[] Data { get; }

    int Length { get; }

    /// <summary>
    /// The UID as stored in the image (9 bytes with check bytes for NTAG215, 7 bytes for NTAG I²C).
    /// </summary>
    byte[] Uid { get; }

    string UidHex { get; }

    /// <summary>
    /// The 8 byte amiibo ID as 16 lowercase hex characters.
    /// </summary>
    string AmiiboIdHex { get; }

    bool HasAmiiboId { get; }

    string AmiiboSeriesHex { get; }

    string TypeHex { get; }

    ushort WriteCounter { get; }

    string GetNickname(AmiiboKeySet? keys);

    string ToQrText();
}

public static class AmiiboImage
{
    public static string Describe(AmiiboFormat format) => format switch
    {
        AmiiboFormat.NtagI2cPlus2k => "amiibo v3 (NTAG I²C Plus 2K)",
        _ => "amiibo (NTAG215)",
    };

    public static bool IsValidLength(int length) => TagDump.IsValidLength(length) || length == AmiiboV3Dump.ImageLength;

    /// <summary>
    /// Recognises a .bin by its size: 532/540/572 bytes are NTAG215 dumps, 2048 bytes are v3 (NTAG I²C Plus 2K) images.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out IAmiiboImage? image)
    {
        if (TagDump.IsValidLength(data.Length))
        {
            image = new TagDump(data);
            return true;
        }

        if (data.Length == AmiiboV3Dump.ImageLength)
        {
            image = new AmiiboV3Dump(data);
            return true;
        }

        image = null;
        return false;
    }

    public static IAmiiboImage Parse(ReadOnlySpan<byte> data) =>
        TryParse(data, out var image)
            ? image!
            : throw new TagException($"Unrecognised tag image ({data.Length} bytes). Expected 532, 540 or 572 bytes (NTAG215) or 2048 bytes (amiibo v3).");

    /// <summary>
    /// Parses QR code text (base64 of the image), as produced by the Swift app.
    /// </summary>
    public static IAmiiboImage FromQrText(string text)
    {
        byte[] bytes;
        try
        {
            var cleaned = new string(text.Where(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '=').ToArray());
            bytes = Convert.FromBase64String(cleaned);
        }
        catch (FormatException ex)
        {
            throw new TagException("Failed to decode QR code.", ex);
        }

        return TryParse(bytes, out var image) ? image! : throw new TagException("Invalid data in QR code.");
    }
}
