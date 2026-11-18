using System.Threading.Channels;
using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;

namespace AmiiTag.Tests.Simulation;

/// <summary>
/// An MFRC522 wired to a USB-serial adapter in UART mode, simulated at register level from the NXP datasheet:
/// <list type="bullet">
/// <item>UART framing (section 8.1.3): a read is an address byte with bit 7 set, answered with the value; a write is
/// address + value, answered with the address. Bytes sent at a different baud rate than the chip's are lost.
/// SerialSpeedReg changes the chip's rate after the acknowledgement. Power-on rate is 9600.</item>
/// <item>64 byte FIFO (FIFOLevelReg with FlushBuffer, BufferOvfl on overflow), CommandReg (Idle, Transceive, CalcCRC,
/// SoftReset), BitFramingReg (TxLastBits, StartSend), ComIrqReg (Set1 write semantics), ErrorReg, ControlReg.RxLastBits,
/// CRC coprocessor, TxControlReg antenna bits, and the timer interrupt when a tag doesn't answer (TModeReg.TAuto).</item>
/// <item>The RF side: WUPA/REQA, the anticollision cascade and SELECT are answered from the tag's UID, ATQA and SAK;
/// HLTA halts the tag; everything else goes to the <see cref="TagSimulator"/> as a frame with CRC_A.</item>
/// </list>
/// </summary>
public sealed class SimulatedMfrc522Bridge : ISerialPortStream
{
    public const int FifoSize = 64;

    private readonly byte[] _registers = new byte[64];
    private readonly List<byte> _fifo = [];
    private readonly Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>();
    private byte[]? _pending;
    private int _pendingOffset;
    private int? _writeAddress;
    private int _anticollisionLevel;
    private List<byte>? _transmitting;
    private int _transmitLastBits;
    private double _wireTime;
    private double _nextRfByteTime;

    // Optional virtual wire clock for exercising UART refill during RF transmission.
    // Each host UART byte takes 10 bits (8N1); an RF data byte takes 9 bits with parity.
    public bool SimulateTransmitTiming { get; set; }
    public TimeSpan InterBatchDelay { get; set; }
    public TimeSpan TransmitStartupDelay { get; set; }
    public int PeakTransmitFifo { get; private set; }
    public List<byte[]> SerialWriteBatches { get; } = [];

    public SimulatedMfrc522Bridge(TagSimulator? tag = null)
    {
        Tag = tag;
        PowerOn();
    }

    /// <summary>
    /// The tag in the field, or null when nothing is present.
    /// </summary>
    public TagSimulator? Tag { get; set; }

    /// <summary>
    /// False simulates a port with nothing connected (no chip answers).
    /// </summary>
    public bool ChipConnected { get; set; } = true;

    public byte Version { get; set; } = 0x92;

    /// <summary>
    /// The chip's current UART rate.
    /// </summary>
    public int ChipBaudRate { get; private set; } = Mfrc522NfcProvider.PowerOnBaudRate;

    /// <summary>
    /// The rate the host opened the port at.
    /// </summary>
    public int HostBaudRate { get; private set; }

    public bool IsOpen { get; private set; }

    /// <summary>
    /// Corrupts the CRC of the next data response from the tag.
    /// </summary>
    public bool CorruptNextTagCrc { get; set; }

    /// <summary>
    /// Models tags seen on real hardware that ignore WUPA once halted: only losing power (the field switched off and on)
    /// brings them back.
    /// </summary>
    public bool HaltedTagIgnoresWupa { get; set; }

    /// <summary>
    /// How many times the RF field has been switched off and on.
    /// </summary>
    public int FieldResets { get; private set; }

    public int RegisterReads { get; private set; }

    public int RegisterWrites { get; private set; }

    /// <summary>
    /// Every frame sent over the air (as the chip transmitted it, CRC included).
    /// </summary>
    public List<byte[]> AirFrames { get; } = [];

    public byte GetRegister(byte register) => _registers[register];

    public bool AntennaOn => (_registers[Mfrc522Reg.TxControl] & Mfrc522Reg.AntennaOn) == Mfrc522Reg.AntennaOn;

    /// <summary>
    /// Power cycle: registers back to their reset values, 9600 baud.
    /// </summary>
    public void PowerOn()
    {
        Array.Clear(_registers);
        _registers[Mfrc522Reg.Command] = 0x20;
        _registers[Mfrc522Reg.ComIEn] = 0x80;
        _registers[Mfrc522Reg.ComIrq] = 0x14;
        _registers[Mfrc522Reg.WaterLevel] = 0x08;
        _registers[Mfrc522Reg.Control] = 0x10;
        _registers[Mfrc522Reg.Coll] = 0xA0;
        _registers[Mfrc522Reg.Mode] = 0x3F;
        _registers[Mfrc522Reg.TxControl] = 0x80;
        _registers[Mfrc522Reg.SerialSpeed] = 0xEB;
        _registers[Mfrc522Reg.CrcResultH] = 0xFF;
        _registers[Mfrc522Reg.CrcResultL] = 0xFF;
        _registers[Mfrc522Reg.ModWidth] = 0x26;
        _fifo.Clear();
        _transmitting = null;
        ChipBaudRate = Mfrc522NfcProvider.PowerOnBaudRate;
    }

    internal void Open(int baudRate)
    {
        IsOpen = true;
        HostBaudRate = baudRate;
        _writeAddress = null;
    }

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!IsOpen)
        {
            throw new IOException("Port is closed.");
        }

        // At the wrong baud rate the chip sees garbage and ignores it.
        if (!ChipConnected || HostBaudRate != ChipBaudRate)
        {
            return Task.CompletedTask;
        }

        if (SimulateTransmitTiming)
        {
            SerialWriteBatches.Add(data.ToArray());
            AdvanceWireTime(InterBatchDelay.TotalSeconds);
        }
        foreach (var value in data)
        {
            if (SimulateTransmitTiming) AdvanceWireTime(10d / HostBaudRate);
            if (_writeAddress is int address)
            {
                _writeAddress = null;
                RegisterWrites++;
                var baudBefore = ChipBaudRate;
                WriteRegister(address, value);
                Send([(byte)address]);
                if (address == Mfrc522Reg.SerialSpeed)
                {
                    ChipBaudRate = Mfrc522Reg.SerialSpeeds.FirstOrDefault(s => s.Value == value).Key is var rate && rate != 0 ? rate : baudBefore;
                }
            }
            else if ((value & 0x80) != 0)
            {
                RegisterReads++;
                Send([ReadRegister(value & 0x3F)]);
            }
            else
            {
                _writeAddress = value & 0x3F;
            }
        }

        return Task.CompletedTask;
    }

    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (SimulateTransmitTiming) AdvanceWireTime(InterBatchDelay.TotalSeconds);
        if (_pending == null)
        {
            _pending = await _toHost.Reader.ReadAsync(cancellationToken);
            _pendingOffset = 0;
        }

        var take = Math.Min(count, _pending.Length - _pendingOffset);
        Array.Copy(_pending, _pendingOffset, buffer, offset, take);
        _pendingOffset += take;
        if (_pendingOffset >= _pending.Length)
        {
            _pending = null;
        }

        return take;
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }

    private void Send(byte[] data) => _toHost.Writer.TryWrite(data);

    // ------------------------------------------------------------------ Registers

    private byte ReadRegister(int address)
    {
        switch (address)
        {
            case Mfrc522Reg.FifoData:
                if (_fifo.Count == 0)
                {
                    return 0;
                }

                var value = _fifo[0];
                _fifo.RemoveAt(0);
                return value;

            case Mfrc522Reg.FifoLevel:
                return (byte)_fifo.Count;

            case Mfrc522Reg.Version:
                return Version;

            default:
                return _registers[address];
        }
    }

    private void WriteRegister(int address, byte value)
    {
        switch (address)
        {
            case Mfrc522Reg.FifoData:
                if (_fifo.Count >= FifoSize)
                {
                    _registers[Mfrc522Reg.Error] |= Mfrc522Reg.ErrBufferOverflow;
                }
                else
                {
                    _fifo.Add(value);
                    if (_transmitting != null) PeakTransmitFifo = Math.Max(PeakTransmitFifo, _fifo.Count);
                }

                break;

            case Mfrc522Reg.FifoLevel:
                if ((value & Mfrc522Reg.FifoFlush) != 0)
                {
                    _fifo.Clear();
                    _registers[Mfrc522Reg.Error] &= unchecked((byte)~Mfrc522Reg.ErrBufferOverflow);
                }

                break;

            case Mfrc522Reg.ComIrq:
                // Set1 = 1 sets the marked bits, Set1 = 0 clears them.
                if ((value & Mfrc522Reg.IrqSet1) != 0)
                {
                    _registers[Mfrc522Reg.ComIrq] |= (byte)(value & 0x7F);
                }
                else
                {
                    _registers[Mfrc522Reg.ComIrq] &= (byte)~(value & 0x7F);
                }

                break;

            case Mfrc522Reg.Command:
                ExecuteCommand(value);
                break;

            case Mfrc522Reg.BitFraming:
                _registers[Mfrc522Reg.BitFraming] = (byte)(value & 0x7F);
                if ((value & Mfrc522Reg.StartSend) != 0 && (_registers[Mfrc522Reg.Command] & 0x0F) == Mfrc522Reg.CmdTransceive)
                {
                    Transceive(value & 0x07);
                }

                break;

            case Mfrc522Reg.Version:
            case Mfrc522Reg.Error:
            case Mfrc522Reg.Status1:
            case Mfrc522Reg.Status2:
                break; // Read only.

            case Mfrc522Reg.TxControl:
                var wasOn = AntennaOn;
                _registers[address] = value;
                if (!wasOn && AntennaOn)
                {
                    // Field back on after being off: the tag powered down and restarts in IDLE.
                    FieldResets++;
                    if (Tag is { IsHalted: true } halted)
                    {
                        halted.Activate();
                    }

                    _anticollisionLevel = 0;
                }

                break;

            default:
                _registers[address] = value;
                break;
        }
    }

    private void ExecuteCommand(byte value)
    {
        var command = value & 0x0F;
        _registers[Mfrc522Reg.Command] = (byte)((_registers[Mfrc522Reg.Command] & 0xF0) | command);
        switch (command)
        {
            case Mfrc522Reg.CmdSoftReset:
                var speed = _registers[Mfrc522Reg.SerialSpeed];
                PowerOn();
                _registers[Mfrc522Reg.SerialSpeed] = speed; // The UART rate isn't changed by a soft reset here.
                ChipBaudRate = HostBaudRate;
                break;

            case Mfrc522Reg.CmdCalcCrc:
                var crc = Crc14443A.Compute(_fifo.ToArray());
                _registers[Mfrc522Reg.CrcResultL] = crc[0];
                _registers[Mfrc522Reg.CrcResultH] = crc[1];
                _registers[Mfrc522Reg.DivIrq] |= 0x04;
                break;

            case Mfrc522Reg.CmdIdle:
                _transmitting = null;
                _registers[Mfrc522Reg.ComIrq] |= Mfrc522Reg.IrqIdle;
                break;
        }
    }

    // ------------------------------------------------------------------ RF

    private void AdvanceWireTime(double seconds)
    {
        var until = _wireTime + seconds;
        while (_transmitting != null && _nextRfByteTime <= until)
        {
            _wireTime = _nextRfByteTime;
            if (_fifo.Count > 0)
            {
                _transmitting.Add(_fifo[0]);
                _fifo.RemoveAt(0);
            }
            if (_fifo.Count == 0)
            {
                var frame = _transmitting.ToArray();
                _transmitting = null;
                FinishTransmit(frame, _transmitLastBits);
            }
            else
                _nextRfByteTime += 9d / 106000;
        }
        _wireTime = until;
    }

    private void Transceive(int txLastBits)
    {
        _registers[Mfrc522Reg.Error] = 0;
        if (SimulateTransmitTiming)
        {
            _transmitting = [];
            _transmitLastBits = txLastBits;
            PeakTransmitFifo = Math.Max(PeakTransmitFifo, _fifo.Count);
            _nextRfByteTime = _wireTime + TransmitStartupDelay.TotalSeconds + 9d / 106000;
            return;
        }
        var frame = _fifo.ToArray();
        _fifo.Clear();
        FinishTransmit(frame, txLastBits);
    }

    private void FinishTransmit(byte[] frame, int txLastBits)
    {
        _registers[Mfrc522Reg.ComIrq] |= Mfrc522Reg.IrqTx;
        AirFrames.Add(frame);

        var response = AntennaOn ? Air(frame, txLastBits) : null;
        if (response == null)
        {
            if ((_registers[Mfrc522Reg.TMode] & Mfrc522Reg.TAuto) != 0)
            {
                _registers[Mfrc522Reg.ComIrq] |= Mfrc522Reg.IrqTimer;
            }

            return;
        }

        var (data, lastBits) = response.Value;
        if (data.Length > FifoSize)
        {
            _fifo.AddRange(data.Take(FifoSize));
            _registers[Mfrc522Reg.Error] |= Mfrc522Reg.ErrBufferOverflow;
            _registers[Mfrc522Reg.ComIrq] |= Mfrc522Reg.IrqErr;
        }
        else
        {
            _fifo.AddRange(data);
        }

        _registers[Mfrc522Reg.Control] = (byte)((_registers[Mfrc522Reg.Control] & 0xF8) | (lastBits & 0x07));
        _registers[Mfrc522Reg.ComIrq] |= Mfrc522Reg.IrqRx;
    }

    /// <summary>
    /// What the tag sends back (null = silence), with the number of valid bits in the last byte (0 = all 8).
    /// </summary>
    private (byte[] Data, int LastBits)? Air(byte[] frame, int txLastBits)
    {
        var tag = Tag;
        if (tag == null || frame.Length == 0)
        {
            return null;
        }

        // Short frames: REQA only wakes idle tags, WUPA wakes halted ones too.
        if (txLastBits == 7 && frame.Length == 1 && frame[0] is 0x26 or 0x52)
        {
            if (tag.IsHalted && (frame[0] == 0x26 || HaltedTagIgnoresWupa))
            {
                return null;
            }

            _anticollisionLevel = 1;
            return (tag.Atqa, 0);
        }

        var uid = tag.Uid7;
        byte[] cl1 = [0x88, uid[0], uid[1], uid[2], (byte)(0x88 ^ uid[0] ^ uid[1] ^ uid[2])];
        byte[] cl2 = [uid[3], uid[4], uid[5], uid[6], (byte)(uid[3] ^ uid[4] ^ uid[5] ^ uid[6])];

        if (_anticollisionLevel > 0 && frame.Length == 2 && frame[1] == 0x20)
        {
            return frame[0] switch
            {
                0x93 when _anticollisionLevel == 1 => (cl1, 0),
                0x95 when _anticollisionLevel == 2 => (cl2, 0),
                _ => null,
            };
        }

        if (_anticollisionLevel > 0 && frame.Length == 9 && frame[1] == 0x70 && Crc14443A.Check(frame))
        {
            if (frame[0] == 0x93 && _anticollisionLevel == 1 && frame.AsSpan(2, 5).SequenceEqual(cl1))
            {
                _anticollisionLevel = 2;
                return (Crc14443A.Append([0x04]), 0); // Cascade bit: UID not complete.
            }

            if (frame[0] == 0x95 && _anticollisionLevel == 2 && frame.AsSpan(2, 5).SequenceEqual(cl2))
            {
                _anticollisionLevel = 0;
                tag.Activate();
                return (Crc14443A.Append([tag.Sak]), 0);
            }

            return null;
        }

        if (frame.Length == 4 && frame[0] == 0x50 && frame[1] == 0x00 && Crc14443A.Check(frame))
        {
            tag.Halt();
            return null;
        }

        var response = tag.TransceiveFrame(frame);
        switch (response.Kind)
        {
            case TagResponseKind.None:
                return null;
            case TagResponseKind.Ack:
            case TagResponseKind.Nak:
                return (new[] { response.Nibble }, 4);
        }

        var data = response.Data;
        if (CorruptNextTagCrc)
        {
            CorruptNextTagCrc = false;
            data[^1] ^= 0xFF;
        }

        return (data, 0);
    }
}

public sealed class SimulatedSerialPortFactory : ISerialPortFactory
{
    public SimulatedSerialPortFactory(SimulatedMfrc522Bridge bridge, string portName = "COM7")
    {
        Bridge = bridge;
        PortName = portName;
    }

    public SimulatedMfrc522Bridge Bridge { get; }

    public string PortName { get; }

    public int OpenCount { get; private set; }

    public int? LastBaudRate { get; private set; }

    public List<int> OpenedBaudRates { get; } = [];

    public bool IsSupported => true;

    public Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NfcReaderInfo>>([new NfcReaderInfo(PortName, PortName)]);

    public Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default)
    {
        if (portId != PortName)
        {
            throw new NfcException($"No such port {portId}.");
        }

        OpenCount++;
        LastBaudRate = baudRate;
        OpenedBaudRates.Add(baudRate);
        Bridge.Open(baudRate);
        return Task.FromResult<ISerialPortStream>(Bridge);
    }
}
