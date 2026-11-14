using System.Security.Cryptography;

namespace AmiiTag.Tags;

/// <summary>
/// Helpers for the 9 byte NTAG UID layout (UID0-2, BCC0, UID3-6, BCC1).
/// </summary>
public static class NtagUid
{
    public const int Length = 9;

    /// <summary>
    /// Generates a random NXP style UID with valid check bytes.
    /// </summary>
    public static byte[] Random()
    {
        var data = RandomNumberGenerator.GetBytes(Length);
        data[0] = 0x04;
        data[3] = (byte)(data[0] ^ data[1] ^ data[2] ^ 0x88);
        data[8] = (byte)(data[4] ^ data[5] ^ data[6] ^ data[7]);
        return data;
    }

    /// <summary>
    /// Expands a 7 byte UID (as reported by readers) to the 9 byte layout stored on the tag.
    /// </summary>
    public static byte[] FromSevenByteUid(ReadOnlySpan<byte> uid7)
    {
        if (uid7.Length != 7)
        {
            throw new ArgumentException("UID must be 7 bytes.", nameof(uid7));
        }

        var data = new byte[Length];
        data[0] = uid7[0];
        data[1] = uid7[1];
        data[2] = uid7[2];
        data[3] = (byte)(uid7[0] ^ uid7[1] ^ uid7[2] ^ 0x88);
        data[4] = uid7[3];
        data[5] = uid7[4];
        data[6] = uid7[5];
        data[7] = uid7[6];
        data[8] = (byte)(uid7[3] ^ uid7[4] ^ uid7[5] ^ uid7[6]);
        return data;
    }

    /// <summary>
    /// Returns the 7 byte UID from the 9 byte layout.
    /// </summary>
    public static byte[] ToSevenByteUid(ReadOnlySpan<byte> uid9)
    {
        if (uid9.Length < Length)
        {
            throw new ArgumentException("UID must be 9 bytes.", nameof(uid9));
        }

        return [uid9[0], uid9[1], uid9[2], uid9[4], uid9[5], uid9[6], uid9[7]];
    }

    /// <summary>
    /// The PWD_AUTH password used by amiibo, derived from the UID.
    /// </summary>
    public static byte[] AmiiboPassword(ReadOnlySpan<byte> uid9)
    {
        if (uid9.Length < Length)
        {
            throw new TagException("Invalid UID.");
        }

        return
        [
            (byte)(0xAA ^ (uid9[1] ^ uid9[4])),
            (byte)(0x55 ^ (uid9[2] ^ uid9[5])),
            (byte)(0xAA ^ (uid9[4] ^ uid9[6])),
            (byte)(0x55 ^ (uid9[5] ^ uid9[7])),
        ];
    }
}
