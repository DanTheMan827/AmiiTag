using System.Security.Cryptography;

namespace AmiiTag.Tags;

/// <summary>
/// A "v3" amiibo image: an NXP NTAG I²C Plus 2K (NT3H2211) flattened to 2048 bytes. Layout follows AmiiboZero's amiibo.md.
/// </summary>
/// <remarks>
/// <code>
/// 0x000..0x3FF  Sector 0, pages 00-FF
///   00 UID0-3 | 01 UID4-6, RFU | 02 44 00 LOCK0 LOCK1 | 03 CC | 04..E1 user EEPROM (888 bytes)
///   E2 dynamic lock | E3 AUTH0 | E4 ACCESS | E5 PWD | E6 PACK | E7 PT_I2C | E8..E9 configuration
///   EA..EB invalid | EC..ED session registers | EE..EF invalid | F0..FF SRAM: the 64 byte lock-on response
/// 0x400..0x7FF  Sector 1, pages 00-FF (1024 bytes user EEPROM)
/// </code>
/// The lock-on response lives inside the image at 0x3C0..0x3FF (where the SRAM is mapped), so a .bin that carries
/// one has it, and saving or sharing the image includes it. Unlike NTAG215 there are no BCC bytes in memory, and the
/// amiibo data uses the <see cref="AmiiboCrypto.NtagI2cPlusLayout"/> placement (raw 0x080..0x0BF is preserved as is).
/// </remarks>
public sealed class AmiiboV3Dump : IAmiiboImage, IEquatable<AmiiboV3Dump>
{
    public const int ImageLength = 0x800;
    public const int SectorLength = 0x400;
    public const int UidLength = 7;

    // Sector 0 page numbers.
    public const int DynamicLockPage = 0xE2;
    public const int Auth0Page = 0xE3;
    public const int AccessPage = 0xE4;
    public const int PwdPage = 0xE5;
    public const int PackPage = 0xE6;
    public const int PtI2cPage = 0xE7;
    public const int LastConfigPage = 0xE9;
    public const int SessionPage0 = 0xEC;
    public const int SessionPage1 = 0xED;
    public const int SramFirstPage = 0xF0;
    public const int SramLastPage = 0xFF;

    /// <summary>
    /// Where the lock-on response (SRAM, sector 0 pages F0-FF) is kept in the image.
    /// </summary>
    public const int LockOnOffset = SramFirstPage * 4;

    /// <summary>
    /// GET_VERSION of an NTAG I²C Plus 2K.
    /// </summary>
    public static readonly byte[] Version = [0x00, 0x04, 0x04, 0x05, 0x02, 0x02, 0x15, 0x03];

    private readonly byte[] _data;

    /// <param name="data">The 2048 byte image, including any lock-on response at 0x3C0.</param>
    /// <param name="lockOn">Optionally replaces the lock-on response (see <see cref="WithLockOn"/>).</param>
    public AmiiboV3Dump(ReadOnlySpan<byte> data, ReadOnlySpan<byte> lockOn = default)
    {
        if (data.Length != ImageLength)
        {
            throw new TagException($"A v3 amiibo image must be {ImageLength} bytes (got {data.Length}).");
        }

        _data = data.ToArray();
        if (!lockOn.IsEmpty)
        {
            LockOnResponse.Normalize(lockOn).CopyTo(_data, LockOnOffset);
        }
    }

    public AmiiboFormat Format => AmiiboFormat.NtagI2cPlus2k;

    public byte[] Data => (byte[])_data.Clone();

    public ReadOnlySpan<byte> Span => _data;

    public int Length => _data.Length;

    public byte[] Uid => _data.AsSpan(0, UidLength).ToArray();

    public string UidHex => Hex.ToHex(Uid, prefix: true);

    public string AmiiboIdHex => Hex.ToHex(_data.AsSpan(0x054, 8));

    public byte[] AmiiboId => _data.AsSpan(0x054, 8).ToArray();

    public bool HasAmiiboId => AmiiboIdHex.EndsWith("03", StringComparison.Ordinal);

    public string AmiiboSeriesHex => AmiiboIdHex.Substring(12, 2);

    public string TypeHex => AmiiboIdHex.Substring(6, 2);

    public ushort WriteCounter => (ushort)((_data[0x011] << 8) | _data[0x012]);

    /// <summary>
    /// True when the SRAM area of the image (0x3C0..0x3FF) holds anything.
    /// </summary>
    public bool HasLockOn => _data.AsSpan(LockOnOffset, LockOnResponse.Length).IndexOfAnyExcept((byte)0) >= 0;

    /// <summary>
    /// The 64 byte lock-on response stored in the image, or null if that area is empty.
    /// </summary>
    public byte[]? LockOn => HasLockOn ? _data.AsSpan(LockOnOffset, LockOnResponse.Length).ToArray() : null;

    /// <summary>
    /// True if the stored lock-on response ends with a valid CRC16/MCRF4XX.
    /// </summary>
    public bool LockOnCrcValid => HasLockOn && LockOnResponse.HasValidCrc(_data.AsSpan(LockOnOffset, LockOnResponse.Length));

    public byte[] GetPage(int sector, int page) => _data.AsSpan(sector * SectorLength + page * 4, 4).ToArray();

    /// <summary>
    /// Returns a copy with a different lock-on response: a full 64 byte response is stored as is, a 1-62 byte
    /// payload is padded and given a CRC, and an empty span removes it. The amiibo data itself is untouched.
    /// </summary>
    public AmiiboV3Dump WithLockOn(ReadOnlySpan<byte> lockOn)
    {
        var data = Data;
        if (lockOn.IsEmpty)
        {
            Array.Clear(data, LockOnOffset, LockOnResponse.Length);
        }
        else
        {
            LockOnResponse.Normalize(lockOn).CopyTo(data, LockOnOffset);
        }

        return new AmiiboV3Dump(data);
    }

    public AmiiboV3Dump WithoutLockOn() => WithLockOn(default);

    public byte[] ToInternal() => AmiiboCrypto.ToInternal(_data, AmiiboCrypto.NtagI2cPlusLayout);

    /// <summary>
    /// Decrypted internal data (amiitool layout).
    /// </summary>
    public byte[] Decrypt(AmiiboKeySet keys) => AmiiboCrypto.Decrypt(ToInternal(), keys);

    /// <summary>
    /// True if the HMACs check out with these keys.
    /// </summary>
    public bool Authenticate(AmiiboKeySet keys) => AmiiboCrypto.Authenticate(ToInternal(), keys);

    public string GetNickname(AmiiboKeySet? keys)
    {
        if (keys == null || !HasAmiiboId)
        {
            return string.Empty;
        }

        try
        {
            return AmiiboCrypto.ReadNickname(Decrypt(keys));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Builds an encrypted v3 image from plain internal data, keeping every non-amiibo byte (and the lock-on) of this image.
    /// </summary>
    public AmiiboV3Dump WithPlainInternal(ReadOnlySpan<byte> plain, AmiiboKeySet keys)
    {
        var cipher = AmiiboCrypto.Encrypt(plain, keys);
        return new AmiiboV3Dump(AmiiboCrypto.FromInternal(cipher, _data, AmiiboCrypto.NtagI2cPlusLayout));
    }

    /// <summary>
    /// Moves the figure to a new 7 byte UID (random with the NXP prefix when omitted), re-encrypting the data.
    /// The user data, settings and lock-on response are kept.
    /// </summary>
    public AmiiboV3Dump WithUid(AmiiboKeySet keys, ReadOnlySpan<byte> uid7 = default)
    {
        var uid = uid7.IsEmpty ? RandomUid() : uid7.ToArray();
        if (uid.Length != UidLength)
        {
            throw new TagException("A v3 UID must be 7 bytes.");
        }

        var plain = Decrypt(keys);
        uid.CopyTo(plain, 0x1D4); // Internal 0x1D4..0x1DB mirrors raw 0x000..0x007 (UID + RFU).

        var raw = Data;
        uid.CopyTo(raw, 0);
        return new AmiiboV3Dump(raw).WithPlainInternal(plain, keys);
    }

    /// <summary>
    /// Generates a new v3 figure for an 8 byte ID (ending in 03), using AmiiboZero's template.
    /// </summary>
    /// <param name="id">The amiibo ID.</param>
    /// <param name="keys">Retail keys; without them the image is left unencrypted (not usable on a console).</param>
    /// <param name="uid7">The UID; random (NXP prefix 04) when omitted.</param>
    /// <param name="lockOn">An optional lock-on response or payload.</param>
    public static AmiiboV3Dump Create(ReadOnlySpan<byte> id, AmiiboKeySet? keys, ReadOnlySpan<byte> uid7 = default, ReadOnlySpan<byte> lockOn = default)
    {
        if (id.Length != 8)
        {
            throw new TagException("An amiibo ID must be 8 bytes.");
        }

        var raw = new byte[ImageLength];

        var uid = uid7.IsEmpty ? RandomUid() : uid7.ToArray();
        if (uid.Length != UidLength)
        {
            throw new TagException("A v3 UID must be 7 bytes.");
        }

        uid.CopyTo(raw, 0);
        SetPage(raw, 0x02, 0x44, 0x00, 0x0F, 0xE0);
        SetPage(raw, 0x03, 0xF1, 0x10, 0xFF, 0xEE);
        SetPage(raw, 0x04, 0xA5, 0x00, 0x00, 0x00);
        id.CopyTo(raw.AsSpan(0x054));
        RandomNumberGenerator.GetBytes(32).CopyTo(raw, 0x060);

        SetPage(raw, DynamicLockPage, 0x01, 0x00, 0xFF, 0x00);
        SetPage(raw, Auth0Page, 0x00, 0x00, 0x00, 0x04);
        SetPage(raw, AccessPage, 0x07, 0x00, 0x00, 0x00);
        SetPage(raw, SessionPage0, 0x41, 0x00, 0xF8, 0x48);
        SetPage(raw, SessionPage1, 0x08, 0x01, 0x29, 0x00);

        var image = new AmiiboV3Dump(raw, lockOn);
        return keys == null ? image : image.WithPlainInternal(image.ToInternal(), keys);
    }

    public static AmiiboV3Dump Create(string idHex, AmiiboKeySet? keys) => Create(Hex.FromHex(idHex), keys);

    private static byte[] RandomUid()
    {
        var uid = RandomNumberGenerator.GetBytes(UidLength);
        uid[0] = 0x04;
        return uid;
    }

    private static void SetPage(byte[] raw, int page, byte b0, byte b1, byte b2, byte b3)
    {
        var offset = page * 4;
        raw[offset] = b0;
        raw[offset + 1] = b1;
        raw[offset + 2] = b2;
        raw[offset + 3] = b3;
    }

    public string ToQrText() => Convert.ToBase64String(_data);

    public bool Equals(AmiiboV3Dump? other) => other != null && _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) => obj is AmiiboV3Dump other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_data);
        return hash.ToHashCode();
    }
}
