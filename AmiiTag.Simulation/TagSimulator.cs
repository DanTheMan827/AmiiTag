namespace AmiiTag.Simulation;

public enum TagResponseKind
{
    /// <summary>The tag stayed silent (halted, removed, or ignored the frame).</summary>
    None,

    /// <summary>A 4 bit ACK (0xA).</summary>
    Ack,

    /// <summary>A 4 bit NAK.</summary>
    Nak,

    /// <summary>Byte oriented data.</summary>
    Data,
}

/// <summary>
/// What a tag sends back for one command.
/// </summary>
public readonly record struct TagResponse(TagResponseKind Kind, byte[] Data, byte Nibble)
{
    public const byte AckValue = 0x0A;

    // NAK values from the NTAG21x datasheet.
    public const byte NakInvalidArgument = 0x0;
    public const byte NakCrcError = 0x1;
    public const byte NakAuthCounterOverflow = 0x4;
    public const byte NakEepromWriteError = 0x5;

    public static TagResponse None => new(TagResponseKind.None, [], 0);

    public static TagResponse Ack => new(TagResponseKind.Ack, [], AckValue);

    public static TagResponse Nak(byte value) => new(TagResponseKind.Nak, [], value);

    public static TagResponse Bytes(byte[] data) => new(TagResponseKind.Data, data, 0);
}

/// <summary>
/// An ISO/IEC 14443-A tag at the frame level: CRC_A handling, HALT and wake-up, and halting after a NAK.
/// </summary>
public abstract class TagSimulator
{
    /// <summary>
    /// The 7 byte UID reported during anticollision.
    /// </summary>
    public abstract byte[] Uid7 { get; }

    public virtual byte Sak => 0x00;

    public virtual byte[] Atqa => [0x44, 0x00];

    public bool IsHalted { get; private set; }

    /// <summary>
    /// Every command received (without CRC), for assertions.
    /// </summary>
    public List<byte[]> CommandLog { get; } = [];

    /// <summary>
    /// WUPA + anticollision + SELECT: brings the tag back to ACTIVE (from IDLE or HALT).
    /// </summary>
    public void Activate()
    {
        IsHalted = false;
        OnActivated();
    }

    /// <summary>
    /// HLTA, or removing the tag from the field.
    /// </summary>
    public void Halt()
    {
        IsHalted = true;
        OnHalted();
    }

    /// <summary>
    /// A field reset: power off then reselect.
    /// </summary>
    public void PowerCycle()
    {
        Halt();
        OnPowerCycle();
        Activate();
    }

    /// <summary>
    /// Sends a command without CRC (as Android NfcA, CoreNFC and PC/SC readers do).
    /// </summary>
    public TagResponse Transceive(ReadOnlySpan<byte> command)
    {
        if (IsHalted || command.Length == 0)
        {
            return TagResponse.None;
        }

        CommandLog.Add(command.ToArray());
        var response = Execute(command);

        // NTAG21x: after a NAK the tag returns to IDLE/HALT and must be reactivated.
        if (response.Kind == TagResponseKind.Nak)
        {
            Halt();
        }

        return response;
    }

    /// <summary>
    /// Sends a frame including CRC_A (as an MFRC522 does on the air). Data responses come back with their CRC_A.
    /// </summary>
    public TagResponse TransceiveFrame(ReadOnlySpan<byte> frame)
    {
        if (IsHalted)
        {
            return TagResponse.None;
        }

        if (frame.Length < 3 || !Crc14443A.Check(frame))
        {
            Halt();
            return TagResponse.Nak(TagResponse.NakCrcError);
        }

        var response = Transceive(frame[..^2]);
        return response.Kind == TagResponseKind.Data
            ? TagResponse.Bytes(Crc14443A.Append(response.Data))
            : response;
    }

    protected abstract TagResponse Execute(ReadOnlySpan<byte> command);

    protected virtual void OnActivated()
    {
    }

    protected virtual void OnHalted()
    {
    }

    protected virtual void OnPowerCycle()
    {
    }
}

/// <summary>
/// CRC_A from ISO/IEC 14443-3 (initial value 0x6363, transmitted LSB first).
/// </summary>
public static class Crc14443A
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
