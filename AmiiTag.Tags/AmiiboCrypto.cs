namespace AmiiTag.Tags;

/// <summary>
/// amiitool's "internal" amiibo layout and its encryption, shared by every tag type.
/// Each format only differs in where the internal fields sit in the raw image (see <see cref="Ntag215Layout"/>
/// and <see cref="NtagI2cPlusLayout"/>).
/// </summary>
/// <remarks>
/// Internal layout (0x208 bytes):
/// <code>
/// 0x000..0x007  NTAG data 2 (lock/CC bytes)
/// 0x008..0x027  Data HMAC
/// 0x028..0x02B  Header (A5 magic, write counter, version)
/// 0x02C..0x0DB  Settings          ┐ encrypted (AES-128-CTR, 0x188 bytes)
/// 0x0DC..0x1B3  App data          ┘
/// 0x1B4..0x1D3  Tag HMAC
/// 0x1D4..0x1DB  NTAG data 1 (UID)
/// 0x1DC..0x207  Amiibo ID, ..., keygen salt (0x1E8..0x207)
/// </code>
/// </remarks>
public static class AmiiboCrypto
{
    public const int InternalLength = 0x208;
    public const int EncryptedStart = 0x02C;
    public const int EncryptedLength = 0x188;

    /// <summary>
    /// (internal offset, raw offset, length) for an original NTAG215 amiibo (amiitool's tag ↔ internal mapping).
    /// </summary>
    public static readonly (int Internal, int Raw, int Length)[] Ntag215Layout =
    [
        (0x000, 0x008, 0x008),
        (0x008, 0x080, 0x020),
        (0x028, 0x010, 0x024),
        (0x04C, 0x0A0, 0x168),
        (0x1B4, 0x034, 0x020),
        (0x1D4, 0x000, 0x008),
        (0x1DC, 0x054, 0x02C),
    ];

    /// <summary>
    /// The same fields in a v3 (NTAG I²C Plus 2K) image: the data HMAC and body move +0x40 for the inserted
    /// 64 byte block at raw 0x080..0x0BF (Pixl.js convention, as documented by AmiiboZero).
    /// </summary>
    public static readonly (int Internal, int Raw, int Length)[] NtagI2cPlusLayout =
    [
        (0x000, 0x008, 0x008),
        (0x008, 0x0C0, 0x020),
        (0x028, 0x010, 0x024),
        (0x04C, 0x0E0, 0x168),
        (0x1B4, 0x034, 0x020),
        (0x1D4, 0x000, 0x008),
        (0x1DC, 0x054, 0x02C),
    ];

    public static byte[] ToInternal(ReadOnlySpan<byte> raw, (int Internal, int Raw, int Length)[] layout)
    {
        var internalData = new byte[InternalLength];
        foreach (var (intl, rawOffset, length) in layout)
        {
            raw.Slice(rawOffset, length).CopyTo(internalData.AsSpan(intl, length));
        }

        return internalData;
    }

    /// <summary>
    /// Writes the internal fields back into a copy of <paramref name="raw"/>, leaving every other byte as it was.
    /// </summary>
    public static byte[] FromInternal(ReadOnlySpan<byte> internalData, ReadOnlySpan<byte> raw, (int Internal, int Raw, int Length)[] layout)
    {
        var output = raw.ToArray();
        foreach (var (intl, rawOffset, length) in layout)
        {
            internalData.Slice(intl, length).CopyTo(output.AsSpan(rawOffset, length));
        }

        return output;
    }

    /// <summary>
    /// Decrypts the settings/app data (amiitool's unpack, without the HMAC check).
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> internalData, AmiiboKeySet keys)
    {
        var plain = internalData.ToArray();
        var dataKeys = Derive(keys.DataKey, plain);
        dataKeys.Crypt(plain.AsSpan(EncryptedStart, EncryptedLength)).CopyTo(plain, EncryptedStart);
        return plain;
    }

    /// <summary>
    /// Signs and encrypts plain internal data (amiitool's pack).
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, AmiiboKeySet keys)
    {
        var cipher = plain.ToArray();
        var tagHmac = Derive(keys.StaticKey, plain).Hmac(plain.Slice(0x1D4, 0x34));
        tagHmac.CopyTo(cipher, 0x1B4);

        var dataKeys = Derive(keys.DataKey, plain);
        var hmacInput = new byte[0x18B + 0x20 + 0x34];
        plain.Slice(0x029, 0x18B).CopyTo(hmacInput);
        tagHmac.CopyTo(hmacInput, 0x18B);
        plain.Slice(0x1D4, 0x34).CopyTo(hmacInput.AsSpan(0x18B + 0x20));
        dataKeys.Hmac(hmacInput).CopyTo(cipher, 0x008);

        dataKeys.Crypt(plain.Slice(EncryptedStart, EncryptedLength)).CopyTo(cipher, EncryptedStart);
        return cipher;
    }

    /// <summary>
    /// Re-encrypts <paramref name="sourceInternal"/>'s app data and settings for another tag of the same character.
    /// Everything tied to the target tag comes from <paramref name="targetInternal"/>: the lock/CC bytes, UID, amiibo ID
    /// and keygen salt (all locked on a real figure). Everything else (header with write counter, settings, app data)
    /// comes from the source. The data keys depend on the target's UID and salt, so the result only verifies on that tag,
    /// and its tag HMAC is identical to the one already locked on the target.
    /// </summary>
    public static byte[] RetargetAppData(ReadOnlySpan<byte> sourceInternal, ReadOnlySpan<byte> targetInternal, AmiiboKeySet keys)
    {
        if (!sourceInternal.Slice(0x1DC, 8).SequenceEqual(targetInternal.Slice(0x1DC, 8)))
        {
            throw new TagException("The tag is a different character.");
        }

        var plain = Decrypt(sourceInternal, keys);
        targetInternal[..0x008].CopyTo(plain);
        targetInternal[0x1D4..InternalLength].CopyTo(plain.AsSpan(0x1D4));
        return Encrypt(plain, keys);
    }

    /// <summary>
    /// True when both HMACs of the (encrypted) internal data are valid for these keys.
    /// </summary>
    public static bool Authenticate(ReadOnlySpan<byte> internalData, AmiiboKeySet keys)
    {
        var plain = Decrypt(internalData, keys);
        var expected = Encrypt(plain, keys);
        return expected.AsSpan(0x008, 0x20).SequenceEqual(internalData.Slice(0x008, 0x20)) &&
               expected.AsSpan(0x1B4, 0x20).SequenceEqual(internalData.Slice(0x1B4, 0x20));
    }

    /// <summary>
    /// The settings flags and UTF-16BE nickname from plain internal data.
    /// </summary>
    public static string ReadNickname(ReadOnlySpan<byte> plain)
    {
        if (((plain[0x02E] << 8) | plain[0x02F]) == 0)
        {
            return string.Empty;
        }

        return System.Text.Encoding.BigEndianUnicode.GetString(plain.Slice(0x038, 20)).TrimEnd('\0').Trim();
    }

    private static DerivedTagKey Derive(TagKey key, ReadOnlySpan<byte> internalData) =>
        key.Derive(internalData.Slice(0x1D4, 8), internalData.Slice(0x029, 2), internalData.Slice(0x1E8, 0x20));
}
