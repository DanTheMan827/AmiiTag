using System.Security.Cryptography;
using System.Text;

namespace AmiiTag.Tags;

/// <summary>
/// An NTAG215 memory dump. Port of the Swift <c>TagDump</c> and its extensions.
/// </summary>
/// <remarks>
/// Accepted sizes are 532 (pages 0-132), 540 (pages 0-134) and 572 (540 + 32 byte originality signature).
/// </remarks>
public sealed class TagDump : IAmiiboImage, IEquatable<TagDump>
{
    public const int PageSize = 4;
    public const int PageCount = 135;
    public const int DataLength = 540;
    public const int ShortLength = 532;
    public const int FullLength = 572;
    public const int SignatureLength = 32;

    private static readonly byte[] AmiiboLockBytes = [0x0F, 0xE0, 0xF1, 0x10, 0xFF, 0xEE];
    private static readonly byte[] AmiiboDynamicLock = [0x01, 0x00, 0x0F];
    private static readonly byte[] AmiiboConfig = [0x00, 0x00, 0x00, 0x04, 0x5F, 0x00, 0x00, 0x00];

    private readonly byte[] _data;

    public TagDump(ReadOnlySpan<byte> data)
    {
        if (!IsValidLength(data.Length))
        {
            throw new TagException($"Invalid tag data length ({data.Length} bytes). Expected 532, 540 or 572 bytes.");
        }

        _data = data.ToArray();
    }

    public static bool IsValidLength(int length) => length is ShortLength or DataLength or FullLength;

    public static bool TryCreate(ReadOnlySpan<byte> data, out TagDump? dump)
    {
        dump = IsValidLength(data.Length) ? new TagDump(data) : null;
        return dump != null;
    }

    /// <summary>
    /// A copy of the raw data at its original length.
    /// </summary>
    public byte[] Data => (byte[])_data.Clone();

    public ReadOnlySpan<byte> Span => _data;

    public int Length => _data.Length;

    public AmiiboFormat Format => AmiiboFormat.Ntag215;

    /// <summary>
    /// The data zero padded (or truncated) to <paramref name="length"/> bytes.
    /// </summary>
    public byte[] ToArray(int length = FullLength)
    {
        var output = new byte[length];
        Array.Copy(_data, output, Math.Min(length, _data.Length));
        return output;
    }

    public bool IsLocked => _data[10] != 0 && _data[11] != 0;

    public bool IsAmiibo =>
        _data.AsSpan(10, 6).SequenceEqual(AmiiboLockBytes) &&
        _data.AsSpan(520, 3).SequenceEqual(AmiiboDynamicLock) &&
        _data.AsSpan(524, 8).SequenceEqual(AmiiboConfig);

    /// <summary>
    /// The full 9 byte UID (including both check bytes).
    /// </summary>
    public byte[] Uid => _data.AsSpan(0, NtagUid.Length).ToArray();

    public string UidHex => Hex.ToHex(Uid, prefix: true);

    public byte[]? Signature => _data.Length == FullLength ? _data.AsSpan(DataLength, SignatureLength).ToArray() : null;

    public bool HasSignature => Signature is { } sig && sig.AsSpan().IndexOfAnyExcept((byte)0) >= 0;

    public string HeadHex => Hex.ToHex(_data.AsSpan(84, 4));
    public string TailHex => Hex.ToHex(_data.AsSpan(88, 4));

    /// <summary>
    /// The 8 byte amiibo identification block as 16 lowercase hex characters.
    /// </summary>
    public string AmiiboIdHex => Hex.ToHex(_data.AsSpan(84, 8));

    public byte[] AmiiboId => _data.AsSpan(84, 8).ToArray();

    public string GameSeriesHex => AmiiboIdHex[..3];
    public string CharacterHex => AmiiboIdHex[..4];
    public string TypeHex => AmiiboIdHex.Substring(6, 2);
    public string AmiiboSeriesHex => AmiiboIdHex.Substring(12, 2);

    /// <summary>
    /// True when the identification block looks like a real amiibo ID (they always end in 02).
    /// </summary>
    public bool HasAmiiboId => AmiiboIdHex.EndsWith("02", StringComparison.Ordinal);

    public ushort WriteCounter => (ushort)((_data[17] << 8) | _data[18]);

    /// <summary>
    /// The UID, the internal byte and the signature, as stored by the Swift app in the Signatures folder (42 bytes).
    /// </summary>
    public byte[]? UidSignatureRecord
    {
        get
        {
            var signature = Signature;
            if (signature == null)
            {
                return null;
            }

            var record = new byte[42];
            _data.AsSpan(0, 10).CopyTo(record);
            signature.CopyTo(record, 10);
            return record;
        }
    }

    public bool VerifyOriginality() => Signature is { } sig && NtagOriginality.Verify(Uid, sig);

    private ReadOnlySpan<byte> WriteCounterBytes => _data.AsSpan(17, 2);
    private ReadOnlySpan<byte> KeygenSalt => _data.AsSpan(96, 32);

    /// <summary>
    /// Decrypts the encrypted section (bytes 20-51 followed by 160-519), 392 bytes.
    /// </summary>
    public byte[] Decrypt(AmiiboKeySet keys)
    {
        var derived = keys.DataKey.Derive(Uid, WriteCounterBytes, KeygenSalt);
        return derived.Crypt(EncryptedSection());
    }

    private byte[] EncryptedSection()
    {
        var section = new byte[392];
        _data.AsSpan(20, 32).CopyTo(section);
        _data.AsSpan(160, 360).CopyTo(section.AsSpan(32));
        return section;
    }

    /// <summary>
    /// Returns the amiibo nickname, or an empty string when unset or keys are unavailable.
    /// </summary>
    public string GetNickname(AmiiboKeySet? keys)
    {
        if (keys == null || !IsAmiibo && !HasAmiiboId)
        {
            return string.Empty;
        }

        try
        {
            var decrypted = Decrypt(keys);
            if (((decrypted[2] << 8) | decrypted[3]) == 0)
            {
                return string.Empty;
            }

            return Encoding.BigEndianUnicode.GetString(decrypted, 12, 20).TrimEnd('\0').Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Re-encrypts and re-signs the dump for a new UID. Port of Swift <c>patchedDump(withUID:...)</c>.
    /// </summary>
    /// <param name="newUid">The 9 byte UID of the target tag.</param>
    /// <param name="keys">The retail keys.</param>
    /// <param name="skipDecrypt">True if the encrypted section currently holds plain text (used when generating new tags).</param>
    public TagDump Patch(ReadOnlySpan<byte> newUid, AmiiboKeySet keys, bool skipDecrypt = false)
    {
        if (newUid.Length != NtagUid.Length)
        {
            throw new TagException("Invalid UID.");
        }

        var decrypted = skipDecrypt ? EncryptedSection() : Decrypt(keys);

        var newData = (byte[])_data.Clone();
        newUid.CopyTo(newData);
        decrypted.AsSpan(0, 32).CopyTo(newData.AsSpan(20));
        decrypted.AsSpan(32, 360).CopyTo(newData.AsSpan(160));

        var writeCounter = newData.AsSpan(17, 2).ToArray();
        var salt = newData.AsSpan(96, 32).ToArray();

        var tagKeys = keys.StaticKey.Derive(newUid, writeCounter, salt);
        var tagHmacInput = Concat(newData.AsSpan(0, 8).ToArray(), newData.AsSpan(84, 44).ToArray());
        tagKeys.Hmac(tagHmacInput).CopyTo(newData, 52);

        var dataKeys = keys.DataKey.Derive(newUid, writeCounter, salt);
        var dataHmacInput = Concat(
            newData.AsSpan(17, 35).ToArray(),
            newData.AsSpan(160, 360).ToArray(),
            newData.AsSpan(52, 32).ToArray(),
            newData.AsSpan(0, 8).ToArray(),
            newData.AsSpan(84, 44).ToArray());
        dataKeys.Hmac(dataHmacInput).CopyTo(newData, 128);

        var encrypted = dataKeys.Crypt(decrypted);
        encrypted.AsSpan(0, 32).CopyTo(newData.AsSpan(20));
        encrypted.AsSpan(32, 360).CopyTo(newData.AsSpan(160));

        return new TagDump(newData);
    }

    /// <summary>
    /// Moves the dump to a different UID (and optionally attaches the matching originality signature).
    /// Port of Swift <c>randomizeUID()</c>.
    /// </summary>
    public TagDump WithUid(ReadOnlySpan<byte> uid9, ReadOnlySpan<byte> signature, AmiiboKeySet keys)
    {
        var patched = Patch(uid9, keys);
        var output = new byte[FullLength];
        patched._data.AsSpan(0, ShortLength).CopyTo(output);
        if (signature.Length == SignatureLength)
        {
            signature.CopyTo(output.AsSpan(DataLength));
        }

        return new TagDump(output);
    }

    /// <summary>
    /// Builds a new, encrypted amiibo for an 8 byte figure ID. Port of Swift <c>TagDump.FromID</c>.
    /// The UID is all zeros, so callers normally follow this with <see cref="WithUid"/>.
    /// </summary>
    public static TagDump FromAmiiboId(ReadOnlySpan<byte> id, AmiiboKeySet? keys)
    {
        if (id.Length != 8)
        {
            throw new TagException("An amiibo ID must be 8 bytes.");
        }

        var dump = new byte[ShortLength];
        byte[] header = [0x00, 0x00, 0x00, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48, 0x0f, 0xe0, 0xf1, 0x10, 0xff, 0xee, 0xa5, 0x00, 0x00, 0x00];
        header.CopyTo(dump, 0);
        id.CopyTo(dump.AsSpan(84));
        RandomNumberGenerator.GetBytes(32).CopyTo(dump, 96);
        byte[] footer = [0x01, 0x00, 0x0F, 0xBD, 0x00, 0x00, 0x00, 0x04, 0x5F, 0x00, 0x00, 0x00];
        footer.CopyTo(dump, 520);

        var tag = new TagDump(dump);
        return keys == null ? tag : tag.Patch(tag.Uid, keys, skipDecrypt: true);
    }

    public static TagDump FromAmiiboId(string idHex, AmiiboKeySet? keys) => FromAmiiboId(Hex.FromHex(idHex), keys);

    /// <summary>
    /// A blank, unlocked NTAG215 with a random UID. Port of Swift <c>TagDump.getBlankTag()</c>.
    /// </summary>
    public static TagDump Blank()
    {
        var tag = new byte[FullLength];
        NtagUid.Random().CopyTo(tag, 0);
        byte[] cc = [0x48, 0x00, 0x00, 0xE1, 0x10, 0x3E, 0x00, 0x03, 0x00, 0xFE];
        cc.CopyTo(tag, 9);
        byte[] cfg = [0xBD, 0x04, 0x00, 0x00, 0xFF, 0x00, 0x05];
        cfg.CopyTo(tag, 523);
        return new TagDump(tag);
    }

    /// <summary>
    /// Parses QR code text (base64 of the dump), as produced by the Swift app.
    /// </summary>
    public static TagDump FromQrText(string text)
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

        if (!IsValidLength(bytes.Length))
        {
            throw new TagException("Invalid data in QR code.");
        }

        return new TagDump(bytes);
    }

    public string ToQrText() => Convert.ToBase64String(_data);

    private static byte[] Concat(params byte[][] parts)
    {
        var length = parts.Sum(p => p.Length);
        var output = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(output, offset);
            offset += part.Length;
        }

        return output;
    }

    public bool Equals(TagDump? other) => other != null && _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) => obj is TagDump other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_data);
        return hash.ToHashCode();
    }
}
