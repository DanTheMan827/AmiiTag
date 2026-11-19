namespace AmiiTag.Nfc;

/// <summary>
/// CRC_A from ISO/IEC 14443-3 (initial value 6363h, sent least significant byte first).
/// </summary>
public static class Iso14443Crc
{
    public static byte[] Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0x6363;
        foreach (var value in data)
        {
            var ch = (byte)(value ^ (byte)(crc & 0xFF));
            ch = (byte)(ch ^ (byte)(ch << 4));
            crc = (ushort)((crc >> 8) ^ (ch << 8) ^ (ch << 3) ^ (ch >> 4));
        }

        return [(byte)(crc & 0xFF), (byte)(crc >> 8)];
    }

    public static byte[] Append(ReadOnlySpan<byte> data)
    {
        var crc = Compute(data);
        return [.. data, crc[0], crc[1]];
    }

    public static bool Check(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return false;
        }

        var crc = Compute(frame[..^2]);
        return frame[^2] == crc[0] && frame[^1] == crc[1];
    }
}
