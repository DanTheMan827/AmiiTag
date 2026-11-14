using AmiiTag.Tags;

namespace AmiiTag.Nfc.Mfrc522;

/// <summary>
/// A tag found by <see cref="Mfrc522Reader.SelectAsync"/>.
/// </summary>
public sealed record Mfrc522Card(byte[] Uid, byte[] Atqa, byte Sak);

/// <summary>
/// What came back from one exchange with the tag.
/// </summary>
public readonly record struct Mfrc522Frame(byte[] Data, int ValidBits)
{
    /// <summary>
    /// A 4 bit ACK/NAK.
    /// </summary>
    public bool IsNibble => Data.Length == 1 && ValidBits == 4;
}

/// <summary>
/// Drives an MFRC522 directly through its registers (the host does what a microcontroller library would):
/// RF setup, WUPA/anticollision/SELECT, and raw ISO 14443-A exchanges through the 64 byte FIFO.
/// CRC_A is added and checked here, so 4 bit ACK/NAK answers can be told apart from data.
/// </summary>
public sealed class Mfrc522Reader
{
    public const int FifoSize = 64;

    private readonly Mfrc522Uart _uart;

    public Mfrc522Reader(Mfrc522Uart uart)
    {
        _uart = uart;
    }

    public Mfrc522Uart Uart => _uart;

    /// <summary>
    /// How long an exchange may take before it's treated as "no answer" (the chip's own timer is 25 ms).
    /// </summary>
    public TimeSpan ExchangeTimeout { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// VersionReg, or null if nothing answered (wrong baud rate, nothing connected).
    /// </summary>
    public async Task<byte?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _uart.ReadAsync(Mfrc522Reg.Version, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the chip in a known state: idle, FIFO empty, 106 kbit/s without hardware CRC, a 25 ms response timer,
    /// 100% ASK modulation, CRC preset 6363h, and the antenna on.
    /// </summary>
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        await _uart.WriteManyAsync(
        [
            (Mfrc522Reg.Command, Mfrc522Reg.CmdIdle),
            (Mfrc522Reg.ComIrq, Mfrc522Reg.IrqAll),
            (Mfrc522Reg.FifoLevel, Mfrc522Reg.FifoFlush),
            (Mfrc522Reg.TxMode, 0x00),
            (Mfrc522Reg.RxMode, 0x00),
            (Mfrc522Reg.ModWidth, 0x26),
            (Mfrc522Reg.TMode, Mfrc522Reg.TAuto),   // Timer starts when transmission ends.
            (Mfrc522Reg.TPrescaler, 0xA9),           // 13.56 MHz / (2 * 169 + 1) = 40 kHz, 25 µs per tick.
            (Mfrc522Reg.TReloadH, 0x03),
            (Mfrc522Reg.TReloadL, 0xE8),             // 1000 ticks = 25 ms.
            (Mfrc522Reg.TxAsk, 0x40),                // Force 100% ASK.
            (Mfrc522Reg.Mode, 0x3D),                 // CRC preset 6363h.
            (Mfrc522Reg.Coll, 0x00),                 // Keep received bits after a collision cleared.

            // Strongest field and most sensitive receiver. v3 amiibo (NTAG I²C Plus) also power the figure's own
            // microcontroller from the field, and on cheap boards at the reset settings they can brown out part way
            // through a read and stop answering.
            (Mfrc522Reg.RfCfg, Mfrc522Reg.RxGainMax),
            (Mfrc522Reg.GsN, Mfrc522Reg.GsNMax),
            (Mfrc522Reg.CWGsP, Mfrc522Reg.CWGsPMax),
        ], cancellationToken);

        await _uart.SetBitsAsync(Mfrc522Reg.TxControl, Mfrc522Reg.AntennaOn, cancellationToken);
    }

    /// <summary>
    /// WUPA (wakes tags that are idle or halted), then anticollision and SELECT through the cascade levels.
    /// Returns null if no tag answered.
    /// </summary>
    public async Task<Mfrc522Card?> SelectAsync(CancellationToken cancellationToken = default)
    {
        var atqa = await ExchangeAsync([0x52], txLastBits: 7, cancellationToken);
        if (atqa == null || atqa.Value.Data.Length != 2)
        {
            return null;
        }

        var uid = new List<byte>();
        byte sak = 0;
        foreach (var cascade in new byte[] { 0x93, 0x95, 0x97 })
        {
            var anticollision = await ExchangeAsync([cascade, 0x20], 0, cancellationToken);
            if (anticollision is not { Data.Length: 5 } ac || (ac.Data[0] ^ ac.Data[1] ^ ac.Data[2] ^ ac.Data[3]) != ac.Data[4])
            {
                return null;
            }

            byte[] select = Iso14443Crc.Append([cascade, 0x70, .. ac.Data]);
            var selected = await ExchangeAsync(select, 0, cancellationToken);
            if (selected is not { Data.Length: 3 } sa || !Iso14443Crc.Check(sa.Data))
            {
                return null;
            }

            sak = sa.Data[0];
            System.Diagnostics.Debug.WriteLine($"MFRC522: cascade {cascade:X2} -> {Hex.ToSpacedHex(ac.Data)}, SAK {sak:X2}");
            var cascadeTag = ac.Data[0] == 0x88 && (sak & 0x04) != 0;
            uid.AddRange(cascadeTag ? ac.Data.AsSpan(1, 3).ToArray() : ac.Data.AsSpan(0, 4).ToArray());
            if ((sak & 0x04) == 0)
            {
                return new Mfrc522Card(uid.ToArray(), atqa.Value.Data, sak);
            }
        }

        return null;
    }

    /// <summary>
    /// Sends a command (CRC_A is appended) and returns the tag's answer without its CRC, a 4 bit ACK/NAK as one byte.
    /// </summary>
    public async Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
    {
        var frame = Iso14443Crc.Append(command);
        if (frame.Length > FifoSize)
        {
            // Nothing is sent (FAST_WRITE of 16 pages is 69 bytes with its CRC); callers fall back to smaller commands.
            throw new NfcCommandNotSupportedException(command[0]);
        }

        var answer = await ExchangeAsync(frame, 0, cancellationToken);
        if (answer is not { } response)
        {
            System.Diagnostics.Debug.WriteLine($"MFRC522 > {Hex.ToSpacedHex(command)}  < (no answer)");
            throw new NfcException($"The tag didn't respond (to {DescribeCommand(command)}).");
        }

        System.Diagnostics.Debug.WriteLine(
            $"MFRC522 > {Hex.ToSpacedHex(command)}  < {Hex.ToSpacedHex(response.Data)}{(response.IsNibble ? " (4 bits)" : string.Empty)}");

        if (response.IsNibble)
        {
            return [(byte)(response.Data[0] & 0x0F)];
        }

        if (response.Data.Length < 3 || !Iso14443Crc.Check(response.Data))
        {
            throw new NfcException("The tag's answer had a CRC error.");
        }

        return response.Data.AsSpan(0, response.Data.Length - 2).ToArray();
    }

    /// <summary>
    /// Names a command for error messages: "FAST_READ 30-3E", "READ 15"...
    /// </summary>
    private static string DescribeCommand(byte[] command) => command switch
    {
        [0x30, var page] => $"READ {page:X2}",
        [0x3A, var start, var end] => $"FAST_READ {start:X2}-{end:X2}",
        [0x60] => "GET_VERSION",
        [0x3C, ..] => "READ_SIG",
        [0xA2, var page, ..] => $"WRITE {page:X2}",
        [0x1B, ..] => "PWD_AUTH",
        [0xC2, 0xFF] => "SECTOR_SELECT",
        _ => Hex.ToSpacedHex(command),
    };

    /// <summary>
    /// HLTA. The tag doesn't answer; it stays halted until the next WUPA.
    /// </summary>
    public async Task HaltAsync(CancellationToken cancellationToken = default)
    {
        await ExchangeAsync(Iso14443Crc.Append([0x50, 0x00]), 0, cancellationToken);
    }

    public async Task AntennaOffAsync(CancellationToken cancellationToken = default) =>
        await _uart.ClearBitsAsync(Mfrc522Reg.TxControl, Mfrc522Reg.AntennaOn, cancellationToken);

    /// <summary>
    /// Switches the RF field off and on again. Any tag in the field loses power and restarts in IDLE, whatever state it
    /// was left in (halted, half way through anticollision, or stuck after an interrupted command).
    /// </summary>
    public async Task ResetFieldAsync(CancellationToken cancellationToken = default)
    {
        await AntennaOffAsync(cancellationToken);
        await Task.Delay(10, cancellationToken);
        await _uart.SetBitsAsync(Mfrc522Reg.TxControl, Mfrc522Reg.AntennaOn, cancellationToken);
        await Task.Delay(5, cancellationToken); // Let the tag power up before the next WUPA.
    }

    /// <summary>
    /// One raw exchange (no CRC handling): the frame goes through the FIFO with the Transceive command, then the
    /// interrupt flags are polled until data arrives or the chip's timer runs out. Null means no answer.
    /// </summary>
    public async Task<Mfrc522Frame?> ExchangeAsync(byte[] frame, byte txLastBits, CancellationToken cancellationToken = default)
    {
        var writes = new List<(byte, byte)>
        {
            (Mfrc522Reg.Command, Mfrc522Reg.CmdIdle),
            (Mfrc522Reg.ComIrq, Mfrc522Reg.IrqAll),
            (Mfrc522Reg.FifoLevel, Mfrc522Reg.FifoFlush),
        };
        writes.AddRange(frame.Select(b => (Mfrc522Reg.FifoData, b)));
        writes.Add((Mfrc522Reg.BitFraming, txLastBits));
        writes.Add((Mfrc522Reg.Command, Mfrc522Reg.CmdTransceive));
        writes.Add((Mfrc522Reg.BitFraming, (byte)(Mfrc522Reg.StartSend | txLastBits)));
        await _uart.WriteManyAsync(writes, cancellationToken);

        var deadline = DateTime.UtcNow + ExchangeTimeout;
        while (true)
        {
            var irq = await _uart.ReadAsync(Mfrc522Reg.ComIrq, cancellationToken);
            if ((irq & (Mfrc522Reg.IrqRx | Mfrc522Reg.IrqIdle)) != 0)
            {
                break;
            }

            if ((irq & Mfrc522Reg.IrqTimer) != 0 || DateTime.UtcNow > deadline)
            {
                await _uart.WriteAsync(Mfrc522Reg.Command, Mfrc522Reg.CmdIdle, cancellationToken);
                return null;
            }
        }

        var status = await _uart.ReadManyAsync([Mfrc522Reg.Error, Mfrc522Reg.FifoLevel, Mfrc522Reg.Control], cancellationToken);
        var error = status[0];
        if ((error & Mfrc522Reg.ErrBufferOverflow) != 0)
        {
            throw new NfcException("The tag's answer didn't fit in the MFRC522's 64 byte FIFO.");
        }

        if ((error & Mfrc522Reg.ErrCollision) != 0)
        {
            throw new NfcException("More than one tag is on the reader.");
        }

        if ((error & (Mfrc522Reg.ErrParity | Mfrc522Reg.ErrProtocol)) != 0)
        {
            throw new NfcException("The tag's answer was garbled (parity or protocol error).");
        }

        var length = status[1] & 0x7F;
        var data = await _uart.ReadFifoAsync(length, cancellationToken);
        var lastBits = status[2] & Mfrc522Reg.RxLastBitsMask;
        return new Mfrc522Frame(data, lastBits == 0 ? 8 : lastBits);
    }
}
