using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace AmiiTag.Tags;

/// <summary>
/// One half of key_retail.bin (80 bytes). Port of the Swift <c>TagKey</c>.
/// </summary>
public sealed class TagKey
{
    public const int Length = 80;

    private readonly byte[] _data;

    public TagKey(ReadOnlySpan<byte> data)
    {
        if (data.Length != Length)
        {
            throw new ArgumentException($"A tag key must be {Length} bytes.", nameof(data));
        }

        if (data[31] > 16)
        {
            throw new ArgumentException("Invalid magic byte size in key.", nameof(data));
        }

        _data = data.ToArray();
    }

    public ReadOnlySpan<byte> HmacKey => _data.AsSpan(0, 16);
    public ReadOnlySpan<byte> TypeString => _data.AsSpan(16, 14);
    public byte MagicBytesSize => _data[31];
    public ReadOnlySpan<byte> MagicBytes => _data.AsSpan(32, MagicBytesSize);
    public ReadOnlySpan<byte> XorPad => _data.AsSpan(48, 32);

    public byte[] ToArray() => (byte[])_data.Clone();

    /// <summary>
    /// Derives the AES key, IV and HMAC key for a tag (amiitool's keygen).
    /// </summary>
    public DerivedTagKey Derive(ReadOnlySpan<byte> uid, ReadOnlySpan<byte> writeCounter, ReadOnlySpan<byte> salt)
    {
        if (uid.Length < 8)
        {
            throw new ArgumentException("UID must be at least 8 bytes.", nameof(uid));
        }

        if (salt.Length != 32)
        {
            throw new ArgumentException("Salt must be 32 bytes.", nameof(salt));
        }

        var seed = new List<byte>(96);
        seed.AddRange(TypeString.ToArray());
        if (MagicBytesSize < 16)
        {
            seed.AddRange(writeCounter.ToArray());
        }

        seed.AddRange(MagicBytes.ToArray());
        seed.AddRange(uid[..8].ToArray());
        seed.AddRange(uid[..8].ToArray());
        for (var i = 0; i < 32; i++)
        {
            seed.Add((byte)(salt[i] ^ XorPad[i]));
        }

        var seedBytes = seed.ToArray();
        var block0 = DrbgStep(seedBytes, 0);
        var block1 = DrbgStep(seedBytes, 1);

        var output = new byte[48];
        block0.CopyTo(output, 0);
        Array.Copy(block1, 0, output, 32, 16);

        return new DerivedTagKey(output.AsSpan(0, 16).ToArray(), output.AsSpan(16, 16).ToArray(), output.AsSpan(32, 16).ToArray());
    }

    private byte[] DrbgStep(byte[] seed, byte iteration)
    {
        var input = new byte[seed.Length + 2];
        input[0] = (byte)((iteration >> 8) & 0x0f);
        input[1] = (byte)(iteration & 0x0f);
        seed.CopyTo(input, 2);
        return Crypto.HmacSha256(HmacKey, input);
    }
}

/// <summary>
/// Keys derived for a specific tag. Port of the Swift <c>DerivedTagKey</c>.
/// </summary>
public sealed class DerivedTagKey
{
    internal DerivedTagKey(byte[] aesKey, byte[] aesIv, byte[] hmacKey)
    {
        AesKey = aesKey;
        AesIv = aesIv;
        HmacKey = hmacKey;
    }

    public byte[] AesKey { get; }
    public byte[] AesIv { get; }
    public byte[] HmacKey { get; }

    public byte[] Hmac(ReadOnlySpan<byte> input) => Crypto.HmacSha256(HmacKey, input);

    /// <summary>
    /// AES-128-CTR. Encryption and decryption are the same operation.
    /// </summary>
    public byte[] Crypt(ReadOnlySpan<byte> input) => Crypto.AesCtr(AesKey, AesIv, input);
}

/// <summary>
/// The pair of keys contained in key_retail.bin: the "unfixed info" data key followed by the "locked secret" static key.
/// They can also come from the two split files (unfixed-info.bin and locked-secret.bin, 80 bytes each). Decrypting only
/// needs the unfixed (data) key; encrypting and signing need both.
/// </summary>
public sealed class AmiiboKeySet
{
    public const int FileLength = TagKey.Length * 2;

    /// <summary>
    /// SHA-256 of a known good key_retail.bin, taken from the Swift app.
    /// </summary>
    private static readonly byte[] ValidHash =
    [
        0x7f, 0x92, 0x83, 0x65, 0x4e, 0xc1, 0x09, 0x7f,
        0xbd, 0xff, 0x31, 0xde, 0x94, 0x66, 0x51, 0xae,
        0x60, 0xc2, 0x85, 0x4a, 0xfb, 0x54, 0x4a, 0xbe,
        0x89, 0x63, 0xd3, 0x89, 0x63, 0x9c, 0x71, 0x0e,
    ];

    private readonly TagKey? _staticKey;
    private readonly byte[]? _raw;

    private AmiiboKeySet(TagKey dataKey, TagKey? staticKey, byte[]? raw)
    {
        DataKey = dataKey;
        _staticKey = staticKey;
        _raw = raw;
    }

    /// <summary>
    /// The "unfixed infos" key (unfixed-info.bin): derives the AES key and data HMAC key.
    /// </summary>
    public TagKey DataKey { get; }

    /// <summary>
    /// The "locked secret" key (locked-secret.bin): derives the tag HMAC key. Missing from a decrypt-only set.
    /// </summary>
    public TagKey StaticKey => _staticKey ?? throw new TagException("Encrypting needs both keys: load locked-secret.bin (or key_retail.bin) too.");

    /// <summary>
    /// False for a decrypt-only set made from unfixed-info.bin alone.
    /// </summary>
    public bool CanEncrypt => _staticKey != null;

    /// <summary>
    /// The 160 bytes of key_retail.bin, as uploaded to pucks running amiitool. Needs both keys.
    /// </summary>
    public byte[] Raw => _raw ?? throw new TagException("Both keys are needed: load locked-secret.bin (or key_retail.bin) too.");

    /// <summary>
    /// Builds the full set from the two split files, each checked against its known hash, and the pair against the
    /// key_retail.bin hash.
    /// </summary>
    public static AmiiboKeySet FromParts(ReadOnlySpan<byte> unfixedInfo, ReadOnlySpan<byte> lockedSecret)
    {
        if (AmiiboKeyFiles.Identify(unfixedInfo) != AmiiboKeyFileKind.UnfixedInfo)
        {
            throw new TagException("That isn't unfixed-info.bin.");
        }

        if (AmiiboKeyFiles.Identify(lockedSecret) != AmiiboKeyFileKind.LockedSecret)
        {
            throw new TagException("That isn't locked-secret.bin.");
        }

        var combined = new byte[FileLength];
        unfixedInfo.CopyTo(combined);
        lockedSecret.CopyTo(combined.AsSpan(TagKey.Length));
        return FromKeyFile(combined);
    }

    /// <summary>
    /// A decrypt-only set from unfixed-info.bin alone (nicknames and app data can be read; nothing can be encrypted).
    /// </summary>
    public static AmiiboKeySet FromUnfixedInfo(ReadOnlySpan<byte> unfixedInfo)
    {
        if (AmiiboKeyFiles.Identify(unfixedInfo) != AmiiboKeyFileKind.UnfixedInfo)
        {
            throw new TagException("That isn't unfixed-info.bin.");
        }

        return new AmiiboKeySet(new TagKey(unfixedInfo), null, null);
    }

    public static bool IsValidKeyFile(ReadOnlySpan<byte> data)
    {
        if (data.Length != FileLength)
        {
            return false;
        }

        return Crypto.Sha256(data).AsSpan().SequenceEqual(ValidHash);
    }

    /// <summary>
    /// Creates the key set from key_retail.bin contents.
    /// </summary>
    /// <param name="data">The 160 byte key file.</param>
    /// <param name="requireKnownHash">If true, the file must match the known retail key hash.</param>
    public static AmiiboKeySet FromKeyFile(ReadOnlySpan<byte> data, bool requireKnownHash = true)
    {
        if (data.Length != FileLength)
        {
            throw new TagException($"The key file must be {FileLength} bytes (got {data.Length}).");
        }

        if (requireKnownHash && !IsValidKeyFile(data))
        {
            throw new TagException("The key file does not match the expected retail keys.");
        }

        return new AmiiboKeySet(new TagKey(data[..80]), new TagKey(data[80..160]), data.ToArray());
    }
}

/// <summary>
/// What a key file is, judged by its size and SHA-256.
/// </summary>
public enum AmiiboKeyFileKind
{
    /// <summary>Not an amiibo key file (wrong size or unknown hash).</summary>
    Unknown,

    /// <summary>key_retail.bin: both keys, 160 bytes.</summary>
    Combined,

    /// <summary>unfixed-info.bin: the data key, 80 bytes (the first half of key_retail.bin).</summary>
    UnfixedInfo,

    /// <summary>locked-secret.bin: the static (tag) key, 80 bytes (the second half of key_retail.bin).</summary>
    LockedSecret,
}

/// <summary>
/// Recognises the retail key files by size and SHA-256. Only hashes are kept here, never key material.
/// </summary>
public static class AmiiboKeyFiles
{
    public const int HalfLength = TagKey.Length;
    public const int CombinedLength = AmiiboKeySet.FileLength;

    /// <summary>
    /// SHA-256 of unfixed-info.bin ("unfixed infos", the first 80 bytes of key_retail.bin).
    /// </summary>
    public static readonly byte[] UnfixedInfoHash = Convert.FromHexString("868106135941cbcab3552bd14880a7a34304ef340958a6998b61a38ba3ce13d3");

    /// <summary>
    /// SHA-256 of locked-secret.bin ("locked secret", the last 80 bytes of key_retail.bin).
    /// </summary>
    public static readonly byte[] LockedSecretHash = Convert.FromHexString("b48727797cd2548200b99c665b20a78190470163ccb8e5682149f1b2f7a006cf");

    public static AmiiboKeyFileKind Identify(ReadOnlySpan<byte> data)
    {
        if (data.Length == CombinedLength)
        {
            return AmiiboKeySet.IsValidKeyFile(data) ? AmiiboKeyFileKind.Combined : AmiiboKeyFileKind.Unknown;
        }

        if (data.Length != HalfLength)
        {
            return AmiiboKeyFileKind.Unknown;
        }

        var hash = Crypto.Sha256(data);
        if (hash.AsSpan().SequenceEqual(UnfixedInfoHash))
        {
            return AmiiboKeyFileKind.UnfixedInfo;
        }

        return hash.AsSpan().SequenceEqual(LockedSecretHash) ? AmiiboKeyFileKind.LockedSecret : AmiiboKeyFileKind.Unknown;
    }

    public static string Describe(AmiiboKeyFileKind kind) => kind switch
    {
        AmiiboKeyFileKind.Combined => "key_retail.bin",
        AmiiboKeyFileKind.UnfixedInfo => "unfixed-info.bin",
        AmiiboKeyFileKind.LockedSecret => "locked-secret.bin",
        _ => "not a key file",
    };
}

/// <summary>
/// Crypto primitives implemented with BouncyCastle so they work identically in the browser.
/// </summary>
internal static class Crypto
{
    public static byte[] HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var hmac = new HMac(new Sha256Digest());
        hmac.Init(new KeyParameter(key.ToArray()));
        var input = data.ToArray();
        hmac.BlockUpdate(input, 0, input.Length);
        var output = new byte[hmac.GetMacSize()];
        hmac.DoFinal(output, 0);
        return output;
    }

    public static byte[] Sha256(ReadOnlySpan<byte> data)
    {
        var digest = new Sha256Digest();
        var input = data.ToArray();
        digest.BlockUpdate(input, 0, input.Length);
        var output = new byte[digest.GetDigestSize()];
        digest.DoFinal(output, 0);
        return output;
    }

    public static byte[] AesCtr(byte[] key, byte[] iv, ReadOnlySpan<byte> input)
    {
        var cipher = new SicBlockCipher(new AesEngine());
        cipher.Init(true, new ParametersWithIV(new KeyParameter(key), iv));

        var inBytes = input.ToArray();
        var output = new byte[inBytes.Length];
        var blockSize = cipher.GetBlockSize();
        var block = new byte[blockSize];
        var outBlock = new byte[blockSize];

        for (var offset = 0; offset < inBytes.Length; offset += blockSize)
        {
            var count = Math.Min(blockSize, inBytes.Length - offset);
            Array.Clear(block);
            Array.Copy(inBytes, offset, block, 0, count);
            cipher.ProcessBlock(block, 0, outBlock, 0);
            Array.Copy(outBlock, 0, output, offset, count);
        }

        return output;
    }
}
