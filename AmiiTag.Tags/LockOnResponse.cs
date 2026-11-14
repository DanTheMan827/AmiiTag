namespace AmiiTag.Tags;

/// <summary>
/// What can be checked about a lock-on response without its cryptography (each figure's response is unique and can't be
/// generated): its length, its CRC, and that it holds data at all.
/// </summary>
public enum LockOnCheck
{
    /// <summary>
    /// 64 bytes with a matching CRC.
    /// </summary>
    Valid,

    /// <summary>
    /// The CRC doesn't match the body (damaged, or a format nothing here knows).
    /// </summary>
    CrcMismatch,

    /// <summary>
    /// Every byte the same (00 or FF): erased SRAM, nothing from a figure.
    /// </summary>
    Blank,

    /// <summary>
    /// Nothing after the first byte: what a reader writes as its request (the figure didn't answer), not a response.
    /// </summary>
    NoData,

    WrongLength,
}

/// <summary>
/// The 64 byte SRAM response a v3 figure's lock-on part serves at sector 0 pages F0-FF (pass-through mode):
/// a 62 byte body followed by its CRC16/MCRF4XX (stored big endian, as Pixl.js does; little endian is accepted too).
/// </summary>
/// <remarks>
/// Layout as described by AmiiboZero: payloads of 1-62 bytes are zero padded to the 62 byte body and given a CRC;
/// a full 64 byte response is kept byte for byte. The CRC's byte order is inferred rather than documented.
/// </remarks>
public static class LockOnResponse
{
    public const int Length = 64;
    public const int BodyLength = 62;

    /// <summary>
    /// Builds a valid 64 byte response from a 1-62 byte payload. A full 64 byte response is returned unchanged
    /// (as read from a figure), whatever its CRC.
    /// </summary>
    public static byte[] Normalize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or (> BodyLength and not Length))
        {
            throw new TagException($"A lock-on payload must be 1-{BodyLength} bytes, or a full {Length} byte response (got {payload.Length}).");
        }

        if (payload.Length == Length)
        {
            return payload.ToArray();
        }

        var response = new byte[Length];
        payload[..Math.Min(payload.Length, BodyLength)].CopyTo(response);
        // Big endian, as the Pixl.js v3 emulator (which consoles accept) stores it at 0x3FE-0x3FF.
        var crc = Crc16Mcrf4xx(response.AsSpan(0, BodyLength));
        response[BodyLength] = (byte)(crc >> 8);
        response[BodyLength + 1] = (byte)crc;
        return response;
    }

    /// <summary>
    /// The checks that can be made without the figure's keys. <see cref="LockOnCheck.Blank"/> and
    /// <see cref="LockOnCheck.NoData"/> are never a figure's response and aren't kept.
    /// </summary>
    public static LockOnCheck Check(ReadOnlySpan<byte> response)
    {
        if (response.Length != Length)
        {
            return LockOnCheck.WrongLength;
        }

        if (response.IndexOfAnyExcept(response[0]) < 0)
        {
            return LockOnCheck.Blank;
        }

        if (response[1..BodyLength].IndexOfAnyExcept((byte)0) < 0)
        {
            return LockOnCheck.NoData;
        }

        return HasValidCrc(response) ? LockOnCheck.Valid : LockOnCheck.CrcMismatch;
    }

    /// <summary>
    /// True for responses worth keeping (<see cref="LockOnCheck.Valid"/>, or <see cref="LockOnCheck.CrcMismatch"/>
    /// with a warning).
    /// </summary>
    public static bool IsUsable(LockOnCheck check) => check is LockOnCheck.Valid or LockOnCheck.CrcMismatch;

    public static string Describe(LockOnCheck check) => check switch
    {
        LockOnCheck.Valid => "CRC valid",
        LockOnCheck.CrcMismatch => "CRC doesn't match",
        LockOnCheck.Blank => "Blank (every byte the same)",
        LockOnCheck.NoData => "No data (a reader's request, not a figure's response)",
        _ => $"Not {Length} bytes",
    };

    public static bool HasValidCrc(ReadOnlySpan<byte> response)
    {
        if (response.Length != Length)
        {
            return false;
        }

        // Either byte order: responses generated before this followed AmiiboZero's notes (little endian).
        var crc = Crc16Mcrf4xx(response[..BodyLength]);
        return (response[BodyLength] == (byte)(crc >> 8) && response[BodyLength + 1] == (byte)crc) ||
               (response[BodyLength] == (byte)crc && response[BodyLength + 1] == (byte)(crc >> 8));
    }

    /// <summary>
    /// CRC-16/MCRF4XX: reflected polynomial 0x8408, initial value 0xFFFF, no final XOR.
    /// </summary>
    public static ushort Crc16Mcrf4xx(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0x8408) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }
}
