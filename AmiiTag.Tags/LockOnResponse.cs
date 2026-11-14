namespace AmiiTag.Tags;

/// <summary>
/// The 64 byte SRAM response a v3 figure's lock-on part serves at sector 0 pages F0-FF (pass-through mode):
/// a 62 byte body followed by its CRC16/MCRF4XX (stored little endian).
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
        var crc = Crc16Mcrf4xx(response.AsSpan(0, BodyLength));
        response[BodyLength] = (byte)crc;
        response[BodyLength + 1] = (byte)(crc >> 8);
        return response;
    }

    public static bool HasValidCrc(ReadOnlySpan<byte> response)
    {
        if (response.Length != Length)
        {
            return false;
        }

        var crc = Crc16Mcrf4xx(response[..BodyLength]);
        return response[BodyLength] == (byte)crc && response[BodyLength + 1] == (byte)(crc >> 8);
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
