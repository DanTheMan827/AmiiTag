using AmiiTag.Tags;

namespace AmiiTag.Tests.Simulation;

/// <summary>
/// An NTAG215 modelled on the NXP NTAG213/215/216 datasheet.
/// </summary>
/// <remarks>
/// Memory (135 pages of 4 bytes):
/// <list type="bullet">
/// <item>Pages 0-1 and bytes 0-1 of page 2: serial number with check bytes, read-only.</item>
/// <item>Page 2 bytes 2-3: static lock bytes (one-time programmable).</item>
/// <item>Page 3: capability container (one-time programmable, bits can only be set).</item>
/// <item>Pages 4-129: user memory.</item>
/// <item>Page 130: dynamic lock bytes 0-2 (one-time programmable) and an RFUI byte.</item>
/// <item>Page 131 CFG0: MIRROR, RFUI, MIRROR_PAGE, AUTH0. Page 132 CFG1: ACCESS, RFUI...</item>
/// <item>Page 133 PWD and page 134 PACK/RFUI: write only, always read as 00h.</item>
/// </list>
/// Commands: GET_VERSION, READ, FAST_READ, WRITE, COMPATIBILITY_WRITE, READ_CNT, PWD_AUTH and READ_SIG.
/// The tag halts after any NAK. AUTH0 and ACCESS are latched when the tag is activated, which is why amiibo
/// writers can set AUTH0 and then still write CFG1 in the same session.
/// </remarks>
public sealed class Ntag215Simulator : TagSimulator
{
    public const int PageCount = 135;
    public const int LastPage = PageCount - 1;
    public const int UserStart = 4;
    public const int UserEnd = 129;
    public const int DynamicLockPage = 130;
    public const int Cfg0Page = 131;
    public const int Cfg1Page = 132;
    public const int PwdPage = 133;
    public const int PackPage = 134;

    public const byte CmdGetVersion = 0x60;
    public const byte CmdRead = 0x30;
    public const byte CmdFastRead = 0x3A;
    public const byte CmdWrite = 0xA2;
    public const byte CmdCompatibilityWrite = 0xA0;
    public const byte CmdReadCnt = 0x39;
    public const byte CmdPwdAuth = 0x1B;
    public const byte CmdReadSig = 0x3C;

    // ACCESS byte (CFG1 byte 0).
    public const byte AccessProt = 0x80;
    public const byte AccessCfgLck = 0x40;
    public const byte AccessNfcCntEn = 0x10;
    public const byte AccessNfcCntPwdProt = 0x08;
    public const byte AccessAuthLimMask = 0x07;

    /// <summary>
    /// GET_VERSION response for NTAG215: header, NXP, NTAG, 50 pF, major 1, minor 0, storage 0x11 (504 bytes), ISO 14443-3.
    /// </summary>
    public static readonly byte[] VersionNtag215 = [0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x11, 0x03];

    private readonly byte[] _memory = new byte[PageCount * 4];
    private readonly byte[] _uid7;

    // Latched at activation.
    private byte _auth0;
    private byte _access;

    private bool _authenticated;
    private bool _counterIncrementedThisActivation;
    private bool _compatibilityWritePending;
    private byte _compatibilityWritePage;

    public Ntag215Simulator(ReadOnlySpan<byte> uid7)
    {
        if (uid7.Length != 7)
        {
            throw new ArgumentException("UID must be 7 bytes.", nameof(uid7));
        }

        _uid7 = uid7.ToArray();
        var uid9 = NtagUid.FromSevenByteUid(uid7);
        uid9.CopyTo(_memory, 0);
        _memory[9] = 0x48; // Internal byte.

        // Factory state.
        byte[] capabilityContainer = [0xE1, 0x10, 0x3E, 0x00];
        capabilityContainer.CopyTo(_memory, 3 * 4);
        byte[] emptyNdef = [0x03, 0x00, 0xFE, 0x00];
        emptyNdef.CopyTo(_memory, 4 * 4);
        _memory[DynamicLockPage * 4 + 3] = 0xBD;
        byte[] cfg0 = [0x04, 0x00, 0x00, 0xFF];
        cfg0.CopyTo(_memory, Cfg0Page * 4);
        byte[] cfg1 = [0x00, 0x05, 0x00, 0x00];
        cfg1.CopyTo(_memory, Cfg1Page * 4);
        byte[] pwd = [0xFF, 0xFF, 0xFF, 0xFF];
        pwd.CopyTo(_memory, PwdPage * 4);

        Activate();
    }

    /// <summary>
    /// A factory fresh NTAG215.
    /// </summary>
    public static Ntag215Simulator CreateBlank(byte[]? uid7 = null) => new(uid7 ?? NtagUid.ToSevenByteUid(NtagUid.Random()));

    /// <summary>
    /// A tag whose raw memory (including PWD/PACK) is already in the given state, e.g. a genuine, locked amiibo.
    /// </summary>
    public static Ntag215Simulator FromMemory(ReadOnlySpan<byte> memory540, byte[]? signature = null)
    {
        if (memory540.Length != PageCount * 4)
        {
            throw new ArgumentException("Memory must be 540 bytes.", nameof(memory540));
        }

        var uid7 = NtagUid.ToSevenByteUid(memory540[..9]);
        var tag = new Ntag215Simulator(uid7);
        memory540.CopyTo(tag._memory);
        if (signature != null)
        {
            tag.Signature = signature;
        }

        tag.Activate();
        return tag;
    }

    /// <summary>
    /// Builds a locked amiibo exactly as it leaves the factory: the dump's data plus PWD derived from the UID and PACK 80 80.
    /// </summary>
    public static Ntag215Simulator FromAmiiboDump(TagDump dump)
    {
        var memory = dump.ToArray(TagDump.DataLength);
        NtagUid.AmiiboPassword(dump.Uid).CopyTo(memory, PwdPage * 4);
        memory[PackPage * 4] = 0x80;
        memory[PackPage * 4 + 1] = 0x80;
        memory[PackPage * 4 + 2] = 0x00;
        memory[PackPage * 4 + 3] = 0x00;
        return FromMemory(memory, dump.Signature);
    }

    public override byte[] Uid7 => (byte[])_uid7.Clone();

    /// <summary>
    /// The 32 byte originality signature returned by READ_SIG.
    /// </summary>
    public byte[] Signature { get; set; } = new byte[32];

    /// <summary>
    /// GET_VERSION response; change it to simulate other tag types.
    /// </summary>
    public byte[] Version { get; set; } = VersionNtag215;

    /// <summary>
    /// The 24 bit NFC counter (READ_CNT).
    /// </summary>
    public int NfcCounter { get; private set; }

    /// <summary>
    /// Negative PWD_AUTH attempts since the last successful one.
    /// </summary>
    public int FailedAuthAttempts { get; private set; }

    public bool IsAuthenticated => _authenticated;

    /// <summary>
    /// The true memory contents (PWD and PACK included), for assertions.
    /// </summary>
    public byte[] Memory => (byte[])_memory.Clone();

    public byte[] GetPage(int page) => _memory.AsSpan(page * 4, 4).ToArray();

    private int AuthLimit
    {
        get
        {
            var authLim = _access & AccessAuthLimMask;
            return authLim == 0 ? int.MaxValue : 1 << authLim;
        }
    }

    private bool AuthLocked => FailedAuthAttempts >= AuthLimit;

    private bool ReadProtected(int page) => (_access & AccessProt) != 0 && !_authenticated && page >= _auth0;

    private bool WriteProtected(int page) => !_authenticated && page >= _auth0;

    protected override void OnActivated()
    {
        _auth0 = _memory[Cfg0Page * 4 + 3];
        _access = _memory[Cfg1Page * 4];
        _authenticated = false;
        _compatibilityWritePending = false;
    }

    protected override void OnHalted()
    {
        _authenticated = false;
        _compatibilityWritePending = false;
    }

    protected override void OnPowerCycle()
    {
        _counterIncrementedThisActivation = false;
    }

    protected override TagResponse Execute(ReadOnlySpan<byte> command)
    {
        if (_compatibilityWritePending)
        {
            _compatibilityWritePending = false;
            return command.Length == 16 ? Write(_compatibilityWritePage, command[..4]) : TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        switch (command[0])
        {
            case CmdGetVersion when command.Length == 1:
                return TagResponse.Bytes((byte[])Version.Clone());

            case CmdRead when command.Length == 2:
                return Read(command[1]);

            case CmdFastRead when command.Length == 3:
                return FastRead(command[1], command[2]);

            case CmdWrite when command.Length == 6:
                return Write(command[1], command[2..6]);

            case CmdCompatibilityWrite when command.Length == 2:
                if (command[1] > LastPage)
                {
                    return TagResponse.Nak(TagResponse.NakInvalidArgument);
                }

                _compatibilityWritePending = true;
                _compatibilityWritePage = command[1];
                return TagResponse.Ack;

            case CmdReadCnt when command.Length == 2:
                return ReadCounter(command[1]);

            case CmdPwdAuth when command.Length == 5:
                return PasswordAuth(command[1..5]);

            case CmdReadSig when command.Length == 2:
                return command[1] == 0x00
                    ? TagResponse.Bytes((byte[])Signature.Clone())
                    : TagResponse.Nak(TagResponse.NakInvalidArgument);

            default:
                return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }
    }

    private byte[] ReadPageForReader(int page)
    {
        // PWD and PACK always read back as zeros.
        return page is PwdPage or PackPage ? new byte[4] : GetPage(page);
    }

    private void CountRead()
    {
        if ((_access & AccessNfcCntEn) != 0 && !_counterIncrementedThisActivation)
        {
            _counterIncrementedThisActivation = true;
            NfcCounter = Math.Min(NfcCounter + 1, 0xFFFFFF);
        }
    }

    private TagResponse Read(int address)
    {
        if (address > LastPage || ReadProtected(address))
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        // READ returns 4 pages and rolls over to page 0 at the end of the accessible area.
        var end = (_access & AccessProt) != 0 && !_authenticated ? Math.Min((int)_auth0, PageCount) : PageCount;
        var data = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            var page = (address + i) % end;
            ReadPageForReader(page).CopyTo(data, i * 4);
        }

        CountRead();
        return TagResponse.Bytes(data);
    }

    private TagResponse FastRead(int start, int end)
    {
        if (start > end || end > LastPage || ReadProtected(end))
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        var data = new byte[(end - start + 1) * 4];
        for (var page = start; page <= end; page++)
        {
            ReadPageForReader(page).CopyTo(data, (page - start) * 4);
        }

        CountRead();
        return TagResponse.Bytes(data);
    }

    private TagResponse ReadCounter(byte address)
    {
        if (address != 0x02 || (_access & AccessNfcCntEn) == 0)
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        if ((_access & AccessNfcCntPwdProt) != 0 && !_authenticated)
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        // Least significant byte first.
        return TagResponse.Bytes([(byte)NfcCounter, (byte)(NfcCounter >> 8), (byte)(NfcCounter >> 16)]);
    }

    private TagResponse PasswordAuth(ReadOnlySpan<byte> password)
    {
        if (AuthLocked)
        {
            return TagResponse.Nak(TagResponse.NakAuthCounterOverflow);
        }

        if (!password.SequenceEqual(_memory.AsSpan(PwdPage * 4, 4)))
        {
            FailedAuthAttempts++;
            return TagResponse.Nak(AuthLocked ? TagResponse.NakAuthCounterOverflow : TagResponse.NakInvalidArgument);
        }

        FailedAuthAttempts = 0;
        _authenticated = true;
        return TagResponse.Bytes(_memory.AsSpan(PackPage * 4, 2).ToArray());
    }

    /// <summary>
    /// Whether page <paramref name="page"/> is read-only because of lock bits or CFGLCK.
    /// </summary>
    public bool IsPageLocked(int page)
    {
        var staticLock0 = _memory[10];
        var staticLock1 = _memory[11];
        var dynamicLock0 = _memory[DynamicLockPage * 4];

        return page switch
        {
            0 or 1 => true,
            2 => false, // Only the lock bytes are writable, and those are one-time programmable.
            3 => (staticLock0 & 0x08) != 0, // L-CC
            >= 4 and <= 7 => (staticLock0 & (0x10 << (page - 4))) != 0, // L4..L7
            >= 8 and <= 15 => (staticLock1 & (0x01 << (page - 8))) != 0, // L8..L15
            >= 16 and <= UserEnd => (dynamicLock0 & (0x01 << ((page - 16) / 16))) != 0, // 16 page granularity
            DynamicLockPage => false,
            Cfg0Page or Cfg1Page => (_access & AccessCfgLck) != 0,
            _ => false,
        };
    }

    private TagResponse Write(int page, ReadOnlySpan<byte> data)
    {
        if (page < 2 || page > LastPage || WriteProtected(page) || IsPageLocked(page))
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        var offset = page * 4;
        switch (page)
        {
            case 2:
                WriteStaticLockBytes(data[2], data[3]);
                break;

            case 3:
                // One-time programmable: bits can be set but never cleared.
                for (var i = 0; i < 4; i++)
                {
                    _memory[offset + i] |= data[i];
                }

                break;

            case DynamicLockPage:
                WriteDynamicLockBytes(data[0], data[1], data[2]);
                break;

            case PackPage:
                // Bytes 2-3 are RFUI.
                _memory[offset] = data[0];
                _memory[offset + 1] = data[1];
                break;

            default:
                data.CopyTo(_memory.AsSpan(offset, 4));
                break;
        }

        return TagResponse.Ack;
    }

    private void WriteStaticLockBytes(byte lock0, byte lock1)
    {
        var current = _memory[10];

        // Block-locking bits freeze lock bits: BL-CC freezes L-CC, BL9-4 freezes L4-L9, BL15-10 freezes L10-L15.
        byte frozen0 = 0;
        byte frozen1 = 0;
        if ((current & 0x01) != 0)
        {
            frozen0 |= 0x08;
        }

        if ((current & 0x02) != 0)
        {
            frozen0 |= 0xF0;
            frozen1 |= 0x03;
        }

        if ((current & 0x04) != 0)
        {
            frozen1 |= 0xFC;
        }

        _memory[10] |= (byte)(lock0 & ~frozen0);
        _memory[11] |= (byte)(lock1 & ~frozen1);
    }

    private void WriteDynamicLockBytes(byte lock0, byte lock1, byte blockLock)
    {
        var offset = DynamicLockPage * 4;

        // Each block-locking bit freezes two dynamic lock bits.
        byte frozen0 = 0;
        var currentBlock = _memory[offset + 2];
        for (var bit = 0; bit < 4; bit++)
        {
            if ((currentBlock & (1 << bit)) != 0)
            {
                frozen0 |= (byte)(0x03 << (bit * 2));
            }
        }

        _memory[offset] |= (byte)(lock0 & ~frozen0);
        _memory[offset + 1] |= lock1;
        _memory[offset + 2] |= blockLock;

        // Byte 3 is RFUI and isn't changed.
    }
}

/// <summary>
/// A puck in NFC write mode: answers FAST_READ 133-134 with 01..08, accepts raw page writes and the 0x88 save command.
/// </summary>
public sealed class PuckNfcSimulator : TagSimulator
{
    private readonly byte[] _uid7;

    public PuckNfcSimulator(byte[]? uid7 = null)
    {
        _uid7 = uid7 ?? [0x04, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
    }

    public override byte[] Uid7 => (byte[])_uid7.Clone();

    public byte[] Memory { get; } = new byte[143 * 4];

    public bool Saved { get; private set; }

    protected override TagResponse Execute(ReadOnlySpan<byte> command)
    {
        switch (command[0])
        {
            case Ntag215Simulator.CmdFastRead when command.Length == 3 && command[1] == 133 && command[2] == 134:
                return TagResponse.Bytes([1, 2, 3, 4, 5, 6, 7, 8]);

            case Ntag215Simulator.CmdWrite when command.Length == 6 && command[1] < 143:
                command[2..6].CopyTo(Memory.AsSpan(command[1] * 4));
                return TagResponse.Ack;

            case 0x88:
                Saved = true;
                return TagResponse.Ack;

            default:
                return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }
    }
}
