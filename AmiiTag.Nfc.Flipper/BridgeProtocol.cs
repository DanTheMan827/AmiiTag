using System.Buffers.Binary;
using System.Text;

namespace AmiiTag.Nfc.Flipper;

/// <summary>
/// Constants of the Flipper NFC bridge protocol, version 2 (mirrors flipper/nfc_bridge/bridge_protocol.h; the
/// full description is flipper/nfc_bridge/docs/PROTOCOL.md).
/// </summary>
public static class BridgeProtocol
{
    public const int Version = 2;
    public const int ReportSize = 64;
    public const int HeaderSize = 6;
    public const int ChunkPayload = ReportSize - HeaderSize;
    public const int MaxPayload = 1024;
    public const int MaxFrame = 900;

    public const int VendorId = 0x1209;
    public const int ProductId = 0x0001;
    public const ushort UsagePage = 0xFF41;
    public const ushort Usage = 0x01;
    public const string ProductName = "NFC Bridge";

    /// <summary>
    /// How long the Flipper waits for an answer before staying silent. Answers normally take well under a millisecond;
    /// this covers a host hiccup: a late answer (a WRITE's ACK, say) usually still satisfies the reader, while silence
    /// makes it give up.
    /// </summary>
    public static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromMilliseconds(100);
}

public enum BridgeMessageType : byte
{
    Hello = 0x01,
    Ack = 0x02,
    FieldOn = 0x10,
    FieldOff = 0x11,
    Activated = 0x12,
    Halted = 0x13,
    Frame = 0x20,
    TransceiveResult = 0x21,
    Status = 0x30,
    Log = 0x7F,

    HostHello = 0x81,
    Listen = 0x82,
    Reader = 0x83,
    Stop = 0x84,
    GetStatus = 0x85,
    SetTiming = 0x86,
    Transceive = 0x88,
    FieldReset = 0x89,
    TableClear = 0x8A,
    TableSet = 0x8B,
    TableRemove = 0x8C,
    Answer = 0x90,
}

public enum BridgeTech : byte
{
    Iso14443A = 0,
    Iso14443B = 1,
    Iso15693 = 2,
    Felica = 3,
}

public enum BridgeStatusCode : byte
{
    Ok = 0,
    BadArguments = 1,
    Unsupported = 2,
    WrongMode = 3,
    TableFull = 4,
    NotFound = 5,
}

[Flags]
public enum BridgeCapabilities : uint
{
    None = 0,
    ListenA = 1 << 0,
    ListenB = 1 << 1,
    ListenV = 1 << 2,
    ListenF = 1 << 3,
    ReaderA = 1 << 4,
    ReaderB = 1 << 5,
    ReaderV = 1 << 6,
    ReaderF = 1 << 7,
    AnswerTable = 1 << 8,
    CustomParity = 1 << 9,
}

[Flags]
public enum BridgeListenFlags : byte
{
    None = 0,

    /// <summary>Check received CRCs; strip valid ones and flag the frame CrcOk.</summary>
    StripCrc = 1 << 0,

    /// <summary>Type A: the Flipper answers HLTA itself and reports HALTED.</summary>
    HandleHlta = 1 << 1,

    /// <summary>Report frames answered from the answer table too.</summary>
    NotifyTableHits = 1 << 2,

    /// <summary>Type A: include received parity bits.</summary>
    ReportParity = 1 << 3,

    Default = StripCrc | HandleHlta | NotifyTableHits,
}

[Flags]
public enum BridgeFrameFlags : byte
{
    None = 0,

    /// <summary>A valid CRC was checked and removed.</summary>
    CrcOk = 1 << 0,

    /// <summary>A parity bitmap follows the data.</summary>
    Parity = 1 << 1,

    /// <summary>Already answered from the answer table.</summary>
    FromTable = 1 << 2,
}

[Flags]
public enum BridgeTxFlags : byte
{
    None = 0,
    AddCrc = 1 << 0,

    /// <summary>Listen: sleep after sending (type A: idle until WUPA).</summary>
    Sleep = 1 << 1,

    CustomParity = 1 << 2,

    /// <summary>Transceive: check and strip the response's CRC.</summary>
    CheckCrc = 1 << 3,
}

public enum BridgeTransceiveKind : byte
{
    Frame = 0,
    Reqa = 1,
    Wupa = 2,
    Sdd = 3,
    CustomParity = 4,
}

public enum BridgeNfcError : byte
{
    None = 0,
    Timeout = 1,
    IncompleteFrame = 2,
    DataFormat = 3,
    Internal = 4,
    WrongMode = 5,
    BadArguments = 6,
}

public readonly record struct BridgeMessage(BridgeMessageType Type, byte Sequence, byte[] Payload);

/// <summary>
/// Splits messages into 64 byte reports and puts them back together.
/// </summary>
public static class BridgeFraming
{
    public static List<byte[]> Encode(BridgeMessageType type, byte sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > BridgeProtocol.MaxPayload)
        {
            throw new ArgumentException($"Bridge messages carry at most {BridgeProtocol.MaxPayload} bytes.", nameof(payload));
        }

        var chunks = Math.Max(1, (payload.Length + BridgeProtocol.ChunkPayload - 1) / BridgeProtocol.ChunkPayload);
        var reports = new List<byte[]>(chunks);
        for (var i = 0; i < chunks; i++)
        {
            var report = new byte[BridgeProtocol.ReportSize];
            report[0] = (byte)type;
            report[1] = sequence;
            report[2] = (byte)i;
            report[3] = (byte)chunks;
            BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4), (ushort)payload.Length);

            var offset = i * BridgeProtocol.ChunkPayload;
            var count = Math.Min(BridgeProtocol.ChunkPayload, payload.Length - offset);
            if (count > 0)
            {
                payload.Slice(offset, count).CopyTo(report.AsSpan(BridgeProtocol.HeaderSize));
            }

            reports.Add(report);
        }

        return reports;
    }
}

/// <summary>
/// Reassembles chunked messages; a missing or out of order chunk drops the message.
/// </summary>
public sealed class BridgeAssembler
{
    private byte[]? _buffer;
    private BridgeMessageType _type;
    private byte _sequence;
    private int _chunks;
    private int _nextChunk = -1;
    private int _received;

    public bool TryAdd(ReadOnlySpan<byte> report, out BridgeMessage message)
    {
        message = default;
        if (report.Length < BridgeProtocol.HeaderSize)
        {
            return false;
        }

        var type = (BridgeMessageType)report[0];
        var sequence = report[1];
        var chunk = report[2];
        var chunks = Math.Max((int)report[3], 1);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(report[4..]);
        if (length > BridgeProtocol.MaxPayload)
        {
            return false;
        }

        if (chunk == 0)
        {
            _buffer = new byte[length];
            _type = type;
            _sequence = sequence;
            _chunks = chunks;
            _received = 0;
        }
        else if (_buffer == null || chunk != _nextChunk || type != _type || sequence != _sequence)
        {
            _nextChunk = -1;
            return false;
        }

        var count = Math.Min(Math.Min(BridgeProtocol.ChunkPayload, _buffer.Length - _received), report.Length - BridgeProtocol.HeaderSize);
        report.Slice(BridgeProtocol.HeaderSize, count).CopyTo(_buffer.AsSpan(_received));
        _received += count;
        _nextChunk = chunk + 1;

        if (_nextChunk < _chunks)
        {
            return false;
        }

        message = new BridgeMessage(_type, _sequence, _buffer);
        _buffer = null;
        _nextChunk = -1;
        return true;
    }
}

internal static class BridgeBits
{
    public static int Bytes(int bits) => (bits + 7) / 8;
}

/// <summary>
/// A frame from the reader (listen mode), as relayed by the Flipper.
/// </summary>
public sealed record BridgeFrame(byte Sequence, uint TimestampMicros, BridgeFrameFlags Flags, int BitCount, byte[] Data, byte[]? Parity = null)
{
    /// <summary>The frame had a valid CRC, which was removed.</summary>
    public bool IsStandard => Flags.HasFlag(BridgeFrameFlags.CrcOk);

    /// <summary>The Flipper already answered it from the answer table.</summary>
    public bool FromTable => Flags.HasFlag(BridgeFrameFlags.FromTable);

    public static BridgeFrame Parse(BridgeMessage message)
    {
        var p = message.Payload;
        if (p.Length < 7)
        {
            throw new InvalidDataException("Short FRAME message.");
        }

        var flags = (BridgeFrameFlags)p[4];
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(5));
        var bytes = Math.Min(BridgeBits.Bytes(bits), p.Length - 7);
        var data = p.AsSpan(7, bytes).ToArray();
        byte[]? parity = null;
        if (flags.HasFlag(BridgeFrameFlags.Parity))
        {
            var count = Math.Min(BridgeBits.Bytes(bytes), p.Length - 7 - bytes);
            parity = p.AsSpan(7 + bytes, count).ToArray();
        }

        return new BridgeFrame(message.Sequence, BinaryPrimitives.ReadUInt32LittleEndian(p), flags, bits, data, parity);
    }

    public byte[] Encode()
    {
        var parity = Parity ?? [];
        var payload = new byte[7 + Data.Length + parity.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, TimestampMicros);
        payload[4] = (byte)(Flags | (Parity != null ? BridgeFrameFlags.Parity : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5), (ushort)BitCount);
        Data.CopyTo(payload, 7);
        parity.CopyTo(payload, 7 + Data.Length);
        return payload;
    }
}

/// <summary>
/// What the Flipper sends back to the reader (an ANSWER or an answer table entry).
/// </summary>
public readonly record struct BridgeAnswer(BridgeTxFlags Flags, int Bits, byte[] Data, byte[]? Parity = null)
{
    public bool IsSilent => Bits == 0;

    public static BridgeAnswer Silence => new(BridgeTxFlags.None, 0, []);

    /// <summary>Send nothing and go to sleep (type A: idle until WUPA).</summary>
    public static BridgeAnswer Idle => new(BridgeTxFlags.Sleep, 0, []);

    /// <summary>Data; the Flipper appends the CRC.</summary>
    public static BridgeAnswer Frame(byte[] data) => new(BridgeTxFlags.AddCrc, data.Length * 8, data);

    /// <summary>Data sent as is (it already carries its CRC, or has none).</summary>
    public static BridgeAnswer RawFrame(byte[] data) => new(BridgeTxFlags.None, data.Length * 8, data);

    /// <summary>A 4 bit ACK/NAK.</summary>
    public static BridgeAnswer Nibble(byte value) => new(BridgeTxFlags.None, 4, [(byte)(value & 0x0F)]);

    /// <summary>A NAK after which the tag goes idle until reselected, as an NTAG does.</summary>
    public static BridgeAnswer NibbleThenIdle(byte value) => new(BridgeTxFlags.Sleep, 4, [(byte)(value & 0x0F)]);

    public byte[] Encode()
    {
        var bytes = BridgeBits.Bytes(Bits);
        var parity = Flags.HasFlag(BridgeTxFlags.CustomParity) ? Parity ?? new byte[BridgeBits.Bytes(bytes)] : [];
        var payload = new byte[3 + bytes + parity.Length];
        payload[0] = (byte)Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)Bits);
        Data.AsSpan(0, Math.Min(bytes, Data.Length)).CopyTo(payload.AsSpan(3));
        parity.CopyTo(payload, 3 + bytes);
        return payload;
    }

    public static BridgeAnswer Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
        {
            return Silence;
        }

        var flags = (BridgeTxFlags)payload[0];
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        var bytes = Math.Min(BridgeBits.Bytes(bits), payload.Length - 3);
        byte[]? parity = flags.HasFlag(BridgeTxFlags.CustomParity) ? payload[(3 + bytes)..].ToArray() : null;
        return new BridgeAnswer(flags, bits, payload.Slice(3, bytes).ToArray(), parity);
    }
}

/// <summary>
/// The outcome of a reader mode TRANSCEIVE.
/// </summary>
public sealed record BridgeTransceiveResult(BridgeNfcError Error, BridgeFrameFlags Flags, int BitCount, TimeSpan Elapsed, byte[] Data, byte[]? Parity = null)
{
    public bool Success => Error == BridgeNfcError.None;

    public bool CrcOk => Flags.HasFlag(BridgeFrameFlags.CrcOk);

    public static BridgeTransceiveResult Parse(byte[] p)
    {
        if (p.Length < 9)
        {
            return new BridgeTransceiveResult(p.Length > 0 ? (BridgeNfcError)p[0] : BridgeNfcError.Internal, 0, 0, TimeSpan.Zero, []);
        }

        var flags = (BridgeFrameFlags)p[1];
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2));
        var bytes = Math.Min(BridgeBits.Bytes(bits), p.Length - 9);
        byte[]? parity = flags.HasFlag(BridgeFrameFlags.Parity) ? p.AsSpan(9 + bytes).ToArray() : null;
        return new BridgeTransceiveResult((BridgeNfcError)p[0], flags, bits,
            TimeSpan.FromMicroseconds(BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4))), p.AsSpan(9, bytes).ToArray(), parity);
    }
}

public sealed record BridgeHello(int ProtocolVersion, int MaxPayload, BridgeCapabilities Capabilities, string Firmware)
{
    public static BridgeHello Parse(byte[] p) => p.Length < 8
        ? throw new InvalidDataException("Short HELLO message; is the Flipper app up to date?")
        : new BridgeHello(p[0], BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(2)),
            (BridgeCapabilities)BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)), Encoding.ASCII.GetString(p, 8, p.Length - 8));
}

/// <summary>
/// The Flipper's counters since the last LISTEN or READER.
/// </summary>
public sealed record BridgeStatus(uint Frames, uint Answered, uint Timeouts, uint LateAnswers, uint WorstRoundTripMicros, uint TableHits, uint Transceives)
{
    public static BridgeStatus Parse(byte[] p)
    {
        if (p.Length < 20)
        {
            throw new InvalidDataException("Short STATUS message.");
        }

        uint At(int i) => p.Length >= (i + 1) * 4 ? BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(i * 4)) : 0;
        return new BridgeStatus(At(0), At(1), At(2), At(3), At(4), At(5), At(6));
    }
}
