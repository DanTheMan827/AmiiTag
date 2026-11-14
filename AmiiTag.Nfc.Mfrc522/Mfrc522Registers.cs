namespace AmiiTag.Nfc.Mfrc522;

/// <summary>
/// MFRC522 register addresses and bits (NXP MFRC522 datasheet, section 9).
/// </summary>
public static class Mfrc522Reg
{
    public const byte Command = 0x01;
    public const byte ComIEn = 0x02;
    public const byte DivIEn = 0x03;
    public const byte ComIrq = 0x04;
    public const byte DivIrq = 0x05;
    public const byte Error = 0x06;
    public const byte Status1 = 0x07;
    public const byte Status2 = 0x08;
    public const byte FifoData = 0x09;
    public const byte FifoLevel = 0x0A;
    public const byte WaterLevel = 0x0B;
    public const byte Control = 0x0C;
    public const byte BitFraming = 0x0D;
    public const byte Coll = 0x0E;
    public const byte Mode = 0x11;
    public const byte TxMode = 0x12;
    public const byte RxMode = 0x13;
    public const byte TxControl = 0x14;
    public const byte TxAsk = 0x15;
    public const byte SerialSpeed = 0x1F;
    public const byte CrcResultH = 0x21;
    public const byte CrcResultL = 0x22;
    public const byte ModWidth = 0x24;
    public const byte RfCfg = 0x26;
    public const byte TMode = 0x2A;
    public const byte TPrescaler = 0x2B;
    public const byte TReloadH = 0x2C;
    public const byte TReloadL = 0x2D;
    public const byte Version = 0x37;

    // CommandReg commands.
    public const byte CmdIdle = 0x00;
    public const byte CmdCalcCrc = 0x03;
    public const byte CmdTransceive = 0x0C;
    public const byte CmdSoftReset = 0x0F;

    // ComIrqReg bits.
    public const byte IrqSet1 = 0x80;
    public const byte IrqTx = 0x40;
    public const byte IrqRx = 0x20;
    public const byte IrqIdle = 0x10;
    public const byte IrqErr = 0x02;
    public const byte IrqTimer = 0x01;
    public const byte IrqAll = 0x7F;

    // ErrorReg bits.
    public const byte ErrBufferOverflow = 0x10;
    public const byte ErrCollision = 0x08;
    public const byte ErrCrc = 0x04;
    public const byte ErrParity = 0x02;
    public const byte ErrProtocol = 0x01;

    // Other bits.
    public const byte FifoFlush = 0x80;
    public const byte StartSend = 0x80;
    public const byte RxLastBitsMask = 0x07;
    public const byte ValuesAfterColl = 0x80;
    public const byte AntennaOn = 0x03;
    public const byte TAuto = 0x80;

    /// <summary>
    /// SerialSpeedReg values for the UART baud rates (datasheet table 10).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, byte> SerialSpeeds = new Dictionary<int, byte>
    {
        [7200] = 0xFA,
        [9600] = 0xEB,
        [14400] = 0xDA,
        [19200] = 0xCB,
        [38400] = 0xAB,
        [57600] = 0x9A,
        [115200] = 0x7A,
        [128000] = 0x74,
        [230400] = 0x5A,
        [460800] = 0x3A,
        [921600] = 0x1C,
        [1228800] = 0x15,
    };

    /// <summary>
    /// VersionReg values seen on MFRC522 chips and common compatible parts.
    /// </summary>
    public static readonly IReadOnlyDictionary<byte, string> KnownVersions = new Dictionary<byte, string>
    {
        [0x88] = "FM17522",
        [0x90] = "MFRC522 v0.0",
        [0x91] = "MFRC522 v1.0",
        [0x92] = "MFRC522 v2.0",
        [0xB2] = "FM17522",
        [0x12] = "MFRC522 compatible",
    };
}
