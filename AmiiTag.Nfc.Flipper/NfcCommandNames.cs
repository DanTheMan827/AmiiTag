namespace AmiiTag.Nfc.Flipper;

/// <summary>
/// Names the NTAG21x / NTAG I2C / N2 Elite commands a reader sends, for the traffic log.
/// </summary>
public static class NfcCommandNames
{
    public static string Describe(ReadOnlySpan<byte> command)
    {
        if (command.IsEmpty)
        {
            return "(empty)";
        }

        return command[0] switch
        {
            0x60 when command.Length == 1 => "GET_VERSION",
            0x30 when command.Length == 2 => $"READ {command[1]:X2}",
            0x3A when command.Length == 3 => $"FAST_READ {command[1]:X2}-{command[2]:X2}",
            0xA2 when command.Length == 6 => $"WRITE {command[1]:X2}",
            0xA0 when command.Length == 2 => $"COMPAT_WRITE {command[1]:X2}",
            0x39 when command.Length == 2 => $"READ_CNT {command[1]:X2}",
            0x1B when command.Length == 5 => "PWD_AUTH",
            0x3C when command.Length == 2 => "READ_SIG",
            0xC2 when command.Length == 2 => "SECTOR_SELECT (1/2)",
            0x50 when command.Length == 2 => "HLTA",
            0x55 when command.Length == 1 => "N2 GET_INFO",
            0x43 when command.Length == 1 => "N2 GET_ID",
            0x3B when command.Length == 4 => $"N2 FAST_READ {command[1]:X2}-{command[2]:X2} bank {command[3]}",
            0xA5 when command.Length == 7 => $"N2 WRITE {command[1]:X2} bank {command[2]}",
            0xA7 when command.Length == 2 => $"N2 SELECT_BANK {command[1]}",
            0xA9 when command.Length == 2 => $"N2 SET_BANK_COUNT {command[1]}",
            0x46 when command.Length == 1 => "N2 LOCK",
            0x44 when command.Length == 1 => "N2 PREPARE_UNLOCK",
            0x45 when command.Length == 1 => "N2 UNLOCK",
            _ when command.Length == 4 => "SECTOR_SELECT (2/2)?",
            _ => $"Unknown {command[0]:X2}",
        };
    }
}
