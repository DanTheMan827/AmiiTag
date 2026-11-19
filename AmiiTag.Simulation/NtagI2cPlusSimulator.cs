using AmiiTag.Tags;

namespace AmiiTag.Simulation;

/// <summary>
/// The NFC interface of an NTAG I²C Plus 2K (NT3H2211), the tag in "v3" amiibo, modelled on the NT3H2111/2211
/// datasheet the same way <see cref="Ntag215Simulator"/> follows the NTAG213/215/216 one.
/// </summary>
/// <remarks>
/// <para><b>Memory (NFC view)</b>, using the page map AmiiboZero documents for amiibo:</para>
/// <list type="bullet">
/// <item>Sector 0: 00h-01h UID (7 bytes, no BCC) + internal byte; 02h internal (2 bytes) + static lock bytes; 03h CC (OTP);
/// 04h-E1h user memory; E2h dynamic lock bytes; E3h AUTH0 (byte 3); E4h ACCESS (byte 0); E5h PWD; E6h PACK (bytes 0-1);
/// E7h PT_I2C (byte 0); E8h-E9h configuration registers; EAh-EBh invalid; ECh-EDh session registers (read only from NFC);
/// EEh-EFh invalid; F0h-FFh SRAM (only while pass-through is on and the I²C side has filled it).</item>
/// <item>Sector 1 (2K only): 00h-FFh user memory. Any other sector doesn't exist.</item>
/// </list>
/// <para><b>Commands</b>: GET_VERSION, READ, FAST_READ, WRITE, COMPATIBILITY_WRITE, PWD_AUTH, READ_SIG, SECTOR_SELECT
/// (C2 FF → ACK, then [sector 00 00 00] → passive ACK, i.e. silence, or NAK) and HLTA. Anything else NAKs.
/// As on NTAG21x, a NAK sends the tag back to IDLE/HALT.</para>
/// <para><b>Rules</b>:</para>
/// <list type="bullet">
/// <item>READ returns 4 pages and rolls over inside the sector (FFh → 00h). A READ or FAST_READ that touches an invalid
/// or read protected page NAKs. FAST_READ needs start ≤ end and stays inside the sector.</item>
/// <item>PWD and PACK always read back as zeros.</item>
/// <item>Static lock bits (page 02h bytes 2-3) and block-locking bits work as on NTAG21x; the CC is OTP; the dynamic lock
/// bytes are OTP and lock sector 0 in 16 page blocks from 10h (byte 0 bits 0-7, byte 1 bits 0-5) and sector 1 in 32
/// page blocks (byte 2), byte 3 being RFU.</item>
/// <item>Password protection covers sector 0 from AUTH0 and all of sector 1, for writes, and for reads too when
/// ACCESS.NFC_PROT is set; AUTH0 above EBh turns it off. AUTHLIM (ACCESS bits 2-0) limits failed PWD_AUTH attempts to
/// 2^AUTHLIM, after which authentication is permanently refused. ACCESS.NFC_DIS_SEC1 makes sector 1 unreachable.
/// AUTH0 and ACCESS are latched when the tag is activated.</item>
/// <item>Configuration registers E8h-E9h are writable (with the password, if protected) until REG_LOCK_NFC
/// (E9h byte 2 bit 0, OTP) is set. Session registers cannot be written from NFC; SRAM accepts WRITE and FAST_WRITE requests.</item>
/// <item>SRAM is request-gated by default. A valid lock-on request starts an I²C processing interval, then
/// NS_REG.SRAM_RF_READY (08h) announces the response. Reading FFh consumes it. Session registers remain readable
/// while I²C owns memory. Other memory accesses NAK (or optionally remain silent) until processing completes.</item>
/// </list>
/// The dynamic lock layout is inferred to match AmiiboZero's template (01 00 FF 00 locks the ID/salt pages and all of
/// sector 1) rather than read from the datasheet.
/// </remarks>
public sealed class NtagI2cPlusSimulator : TagSimulator
{
    public const byte CmdGetVersion = 0x60;
    public const byte CmdRead = 0x30;
    public const byte CmdFastRead = 0x3A;
    public const byte CmdWrite = 0xA2;
    public const byte CmdCompatibilityWrite = 0xA0;
    public const byte CmdPwdAuth = 0x1B;
    public const byte CmdReadSig = 0x3C;
    public const byte CmdSectorSelect = 0xC2;
    public const byte CmdHalt = 0x50;
    public const byte CmdFastWrite = 0xA6;

    public const int SectorPages = 256;
    public const int Sectors = 2;

    public const int StaticLockPage = 0x02;
    public const int CcPage = 0x03;
    public const int UserEnd0 = 0xE1;
    public const int DynamicLockPage = 0xE2;
    public const int Auth0Page = 0xE3;
    public const int AccessPage = 0xE4;
    public const int PwdPage = 0xE5;
    public const int PackPage = 0xE6;
    public const int PtI2cPage = 0xE7;
    public const int ConfigPage0 = 0xE8;
    public const int ConfigPage1 = 0xE9;
    public const int SessionPage0 = 0xEC;
    public const int SessionPage1 = 0xED;
    public const int SramFirstPage = 0xF0;
    public const int SramLastPage = 0xFF;

    public const byte AccessNfcProt = 0x80;
    public const byte AccessNfcDisSec1 = 0x20;
    public const byte AccessAuthLimMask = 0x07;
    public const byte NcPassThroughOn = 0x40;
    public const byte NsSramRfReady = 0x08;
    public const byte NsSramI2cReady = 0x10;
    public const byte NsI2cLocked = 0x40;
    public const byte RegLockNfc = 0x01;

    /// <summary>
    /// Protection is off when AUTH0 is above this page.
    /// </summary>
    public const int LastProtectablePage = 0xEB;

    private readonly byte[][] _sectors = [new byte[SectorPages * 4], new byte[SectorPages * 4]];
    private readonly byte[] _uid7;
    private int _sector;
    private bool _awaitingSector;
    private bool _authenticated;
    private bool _compatibilityWritePending;
    private int _compatibilityWritePage;
    private int _sramReadyAt;
    private readonly TimeProvider _clock;
    private readonly byte[] _request = new byte[64];
    private ushort _requestPages;
    private long _requestStarted;
    private bool _processing;
    private bool _responseReady;

    /// <summary>Require a complete lock-on request before publishing the saved response.</summary>
    public bool RequireLockOnRequest { get; set; } = true;

    /// <summary>Simulated I²C response latency; 10 ms is a profile setting, not a measured original-figure guarantee.</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Optional deterministic fault injection: lose this many request ACKs after accepting the request.</summary>
    public int DropRequestAcknowledgements { get; set; }

    /// <summary>Optional deterministic fault injection: silently drop this many ready SRAM read responses.</summary>
    public int DropSramReadResponses { get; set; }

    /// <summary>Use silence instead of a NAK for memory access during the I²C processing window.</summary>
    public bool SilentWhileProcessing { get; set; }

    public bool IsProcessingRequest { get { UpdateResponse(); return _processing; } }
    public bool IsSramResponseReady { get { UpdateResponse(); return SramReady; } }

    // Latched at activation.
    private int _auth0;
    private byte _access;

    public NtagI2cPlusSimulator(ReadOnlySpan<byte> raw2048, ReadOnlySpan<byte> lockOn = default, TimeProvider? timeProvider = null)
    {
        if (raw2048.Length != AmiiboV3Dump.ImageLength)
        {
            throw new ArgumentException("Expected a 2048 byte image.");
        }

        _clock = timeProvider ?? TimeProvider.System;
        raw2048[..0x400].CopyTo(_sectors[0]);
        raw2048[0x400..].CopyTo(_sectors[1]);
        _uid7 = raw2048[..7].ToArray();
        var storedLockOn = raw2048.Slice(SramFirstPage * 4, 64);

        // Invalid regions and SRAM aren't EEPROM.
        Array.Clear(_sectors[0], 0xEA * 4, 8);
        Array.Clear(_sectors[0], 0xEE * 4, 8);
        Array.Clear(_sectors[0], SramFirstPage * 4, 64);

        if (!lockOn.IsEmpty)
        {
            Sram = LockOnResponse.Normalize(lockOn);
            SramMirrored = true;
        }
        else if (storedLockOn.IndexOfAnyExcept((byte)0) >= 0)
        {
            Sram = storedLockOn.ToArray();
            SramMirrored = true;
        }

        OnActivated();
    }

    public static NtagI2cPlusSimulator FromImage(AmiiboV3Dump image) => new(image.Span);

    /// <summary>
    /// A v3 amiibo as consoles expect it: the image plus PWD derived from the UID and PACK 80 80 (images can't hold the
    /// real values: PWD and PACK always read back as zeros, so a plain <see cref="FromImage"/> refuses consoles), with
    /// sector 1 writable for game saves (<see cref="Sector1Writable"/>).
    /// </summary>
    public static NtagI2cPlusSimulator FromAmiiboImage(AmiiboV3Dump image)
    {
        var raw = image.Span.ToArray();
        NtagUid.AmiiboPassword(NtagUid.FromSevenByteUid(image.Uid)).CopyTo(raw, PwdPage * 4);
        raw[PackPage * 4] = 0x80;
        raw[PackPage * 4 + 1] = 0x80;
        raw[PackPage * 4 + 2] = 0x00;
        raw[PackPage * 4 + 3] = 0x00;
        return new NtagI2cPlusSimulator(raw) { Sector1Writable = true };
    }

    /// <summary>
    /// An unconfigured NT3H2211: zeroed user memory and CC, no locks, protection off (AUTH0 FFh), AUTHLIM off,
    /// pass-through off, and the datasheet's default configuration register values.
    /// </summary>
    public static NtagI2cPlusSimulator CreateBlank(byte[]? uid7 = null)
    {
        var raw = new byte[AmiiboV3Dump.ImageLength];
        (uid7 ?? [0x04, 0x51, 0x62, 0x73, 0x84, 0x95, 0xA6]).CopyTo(raw, 0);
        raw[Auth0Page * 4 + 3] = 0xFF;
        raw[ConfigPage0 * 4 + 2] = 0xF8; // SM_REG
        raw[ConfigPage0 * 4 + 3] = 0x48; // WDT_LS
        raw[ConfigPage1 * 4] = 0x08;     // WDT_MS
        raw[ConfigPage1 * 4 + 1] = 0x01; // I2C_CLOCK_STR
        raw[SessionPage0 * 4 + 2] = 0xF8;
        raw[SessionPage0 * 4 + 3] = 0x48;
        raw[SessionPage1 * 4] = 0x08;
        raw[SessionPage1 * 4 + 1] = 0x01;
        return new NtagI2cPlusSimulator(raw);
    }

    public override byte[] Uid7 => (byte[])_uid7.Clone();

    public byte[] Version { get; set; } = AmiiboV3Dump.Version;

    public byte[] Signature { get; set; } = new byte[32];

    /// <summary>
    /// The saved 64 byte response template. Requests never overwrite this data.
    /// </summary>
    public byte[] Sram { get; set; } = new byte[64];

    /// <summary>
    /// Pass-through is on, so SRAM is at sector 0 pages F0h-FFh.
    /// </summary>
    public bool SramMirrored { get; set; }

    /// <summary>
    /// Legacy replay mode only (RequireLockOnRequest=false): session reads before SRAM becomes ready.
    /// </summary>
    public int SramReadyAfterSessionReads
    {
        get => _sramReadyAt;
        set => _sramReadyAt = value;
    }

    /// <summary>
    /// Legacy replay mode only: session reads before a consumed response is available again.
    /// </summary>
    public int SramRefillSessionReads { get; set; } = 1;

    public int SessionReads { get; private set; }

    public int FailedAuthAttempts { get; private set; }

    public bool IsAuthenticated => _authenticated;

    public int CurrentSector => _sector;

    private bool SramReady => SramMirrored && (RequireLockOnRequest ? _responseReady : SessionReads >= _sramReadyAt);

    private void UpdateResponse()
    {
        if (_processing && _clock.GetElapsedTime(_requestStarted) >= ResponseDelay)
        {
            _processing = false;
            _responseReady = true;
        }
    }

    protected override void OnPowerCycle()
    {
        _processing = false;
        _responseReady = false;
        _requestPages = 0;
        Array.Clear(_request);
        LastSramRequest = null;
    }

    private bool ProtectionEnabled => _auth0 <= LastProtectablePage;

    private int AuthLimit
    {
        get
        {
            var authLim = _access & AccessAuthLimMask;
            return authLim == 0 ? int.MaxValue : 1 << authLim;
        }
    }

    private bool AuthLocked => FailedAuthAttempts >= AuthLimit;

    public byte[] GetPage(int sector, int page) => _sectors[sector].AsSpan(page * 4, 4).ToArray();

    public byte[] SectorMemory(int sector) => (byte[])_sectors[sector].Clone();

    public static bool IsInvalidPage(int sector, int page) =>
        sector == 0 && page is >= 0xEA and <= 0xEB or >= 0xEE and <= 0xEF;

    /// <summary>
    /// Whether a page is read-only because of lock bits (not password protection).
    /// </summary>
    public bool IsPageLocked(int sector, int page)
    {
        var s0 = _sectors[0];
        var lock0 = s0[StaticLockPage * 4 + 2];
        var lock1 = s0[StaticLockPage * 4 + 3];
        var dyn0 = s0[DynamicLockPage * 4];
        var dyn1 = s0[DynamicLockPage * 4 + 1];
        var dyn2 = s0[DynamicLockPage * 4 + 2];

        if (sector == 1)
        {
            return !Sector1Writable && (dyn2 & (1 << (page / 32))) != 0;
        }

        return page switch
        {
            0 or 1 => true,
            StaticLockPage => false, // Only the lock bytes are writable, and they're OTP.
            CcPage => (lock0 & 0x08) != 0,
            >= 4 and <= 7 => (lock0 & (0x10 << (page - 4))) != 0,
            >= 8 and <= 15 => (lock1 & (0x01 << (page - 8))) != 0,
            >= 0x10 and <= 0x8F => (dyn0 & (1 << ((page - 0x10) / 16))) != 0,
            >= 0x90 and <= UserEnd0 => (dyn1 & (1 << ((page - 0x90) / 16))) != 0,
            ConfigPage0 or ConfigPage1 => (s0[ConfigPage1 * 4 + 2] & RegLockNfc) != 0,
            _ => false,
        };
    }

    protected override void OnActivated()
    {
        _sector = 0;
        _awaitingSector = false;
        _authenticated = false;
        _compatibilityWritePending = false;
        _auth0 = _sectors[0][Auth0Page * 4 + 3];
        _access = _sectors[0][AccessPage * 4];
    }

    protected override void OnHalted()
    {
        _authenticated = false;
        _awaitingSector = false;
        _compatibilityWritePending = false;
    }

    protected override TagResponse Execute(ReadOnlySpan<byte> command)
    {
        UpdateResponse();
        if (RequireLockOnRequest && _processing && command[0] is CmdRead or CmdFastRead or CmdWrite or CmdFastWrite or CmdCompatibilityWrite)
        {
            var sessionRead = _sector == 0 && command[0] == CmdFastRead && command.Length == 3 &&
                command[1] >= SessionPage0 && command[2] <= SessionPage1 && command[1] <= command[2];
            if (!sessionRead)
                return SilentWhileProcessing ? TagResponse.None : TagResponse.Nak(TagResponse.NakInvalidArgument);
        }
        if (RequireLockOnRequest && SramReady && DropSramReadResponses > 0 && _sector == 0 &&
            command.Length >= 2 && command[0] is CmdRead or CmdFastRead && command[1] >= SramFirstPage)
        {
            DropSramReadResponses--;
            return TagResponse.None; // Lost RF response: leave SRAM pending for a retry.
        }
        if (_awaitingSector)
        {
            _awaitingSector = false;
            if (command.Length != 4 || command[0] >= Sectors || (command[0] == 1 && (_access & AccessNfcDisSec1) != 0))
            {
                return TagResponse.Nak(TagResponse.NakInvalidArgument);
            }

            _sector = command[0];
            return TagResponse.None; // Passive ACK.
        }

        if (_compatibilityWritePending)
        {
            _compatibilityWritePending = false;
            return command.Length == 16 ? Write(_compatibilityWritePage, command[..4]) : TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        switch (command[0])
        {
            case CmdGetVersion when command.Length == 1:
                return TagResponse.Bytes((byte[])Version.Clone());

            case CmdReadSig when command.Length == 2:
                return command[1] == 0x00 ? TagResponse.Bytes((byte[])Signature.Clone()) : TagResponse.Nak(TagResponse.NakInvalidArgument);

            case CmdSectorSelect when command.Length == 2 && command[1] == 0xFF:
                _awaitingSector = true;
                return TagResponse.Ack;

            case CmdHalt when command.Length == 2 && command[1] == 0x00:
                Halt();
                return TagResponse.None;

            case CmdRead when command.Length == 2:
                return Read(command[1]);

            case CmdFastRead when command.Length == 3:
                return FastRead(command[1], command[2]);

            case CmdWrite when command.Length == 6:
                return Write(command[1], command[2..6]);

            case CmdCompatibilityWrite when command.Length == 2:
                if (IsInvalidPage(_sector, command[1]))
                {
                    return TagResponse.Nak(TagResponse.NakInvalidArgument);
                }

                _compatibilityWritePending = true;
                _compatibilityWritePage = command[1];
                return TagResponse.Ack;

            case CmdPwdAuth when command.Length == 5:
                return PasswordAuth(command[1..5]);

            case CmdFastWrite when command.Length == 3 + 64:
                return FastWrite(command[1], command[2], command[3..]);

            default:
                return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }
    }

    /// <summary>
    /// The last 64 byte request a reader wrote to SRAM with FAST_WRITE (a v3 console's lock-on request).
    /// </summary>
    public byte[]? LastSramRequest { get; private set; }

    /// <summary>
    /// Ignore the sector 1 lock bits (dynamic lock byte 2). Amiibo set them all (E2 reads 01 00 FF 00), yet a Switch
    /// saves game data into sector 1, and the emulators known to work with it (pixl.js, Amiibo Zero) accept those
    /// writes; <see cref="FromAmiiboImage"/> does the same. The lock bytes still read back unchanged.
    /// </summary>
    public bool Sector1Writable { get; set; }

    // Request storage is separate from the saved response, so RF writes cannot corrupt the response template.
    private TagResponse FastWrite(int start, int end, ReadOnlySpan<byte> data)
    {
        if (_sector != 0 || start != SramFirstPage || end != SramLastPage || !SramMirrored || SramProtected)
            return TagResponse.Nak(TagResponse.NakInvalidArgument);

        data.CopyTo(_request);
        return AcceptRequest();
    }

    private bool SramProtected => !_authenticated && (_sectors[0][PtI2cPage * 4] & 0x04) != 0;

    private TagResponse AcceptRequest()
    {
        LastSramRequest = (byte[])_request.Clone();
        _requestPages = 0;
        _responseReady = false;
        _processing = RequireLockOnRequest && _request.AsSpan().SequenceEqual(LockOnResponse.Normalize([0x01]));
        _requestStarted = _clock.GetTimestamp();
        // The NTAG ACKs transport writes even when its attached controller does not understand the payload.
        if (DropRequestAcknowledgements > 0)
        {
            DropRequestAcknowledgements--;
            return TagResponse.None;
        }
        return TagResponse.Ack;
    }

    private bool IsProtected(int page) =>
        ProtectionEnabled && !_authenticated && (_sector == 1 || page >= _auth0);

    private TagResponse Read(int address)
    {
        var data = new byte[16];
        var coversLastSramPage = false;
        for (var i = 0; i < 4; i++)
        {
            var page = (address + i) % SectorPages;
            if (!TryReadPage(page, out var bytes))
            {
                return TagResponse.Nak(TagResponse.NakInvalidArgument);
            }

            bytes.CopyTo(data, i * 4);
            coversLastSramPage |= page == SramLastPage;
        }

        AfterRead(coversLastSramPage);
        return TagResponse.Bytes(data);
    }

    private TagResponse FastRead(int start, int end)
    {
        if (start > end)
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        var data = new byte[(end - start + 1) * 4];
        for (var page = start; page <= end; page++)
        {
            if (!TryReadPage(page, out var bytes))
            {
                return TagResponse.Nak(TagResponse.NakInvalidArgument);
            }

            bytes.CopyTo(data, (page - start) * 4);
        }

        AfterRead(end == SramLastPage);
        return TagResponse.Bytes(data);
    }

    private void AfterRead(bool coversLastSramPage)
    {
        // Reading the last SRAM page hands the SRAM back to the I²C side, which has to fill it again.
        if (_sector == 0 && coversLastSramPage && SramMirrored)
        {
            if (RequireLockOnRequest)
                _responseReady = false; // A new response requires a new request, not more polling.
            else
                _sramReadyAt = SessionReads + SramRefillSessionReads;
        }
    }

    private bool TryReadPage(int page, out byte[] data)
    {
        data = [];
        if (IsInvalidPage(_sector, page))
        {
            return false;
        }

        if (_sector == 0)
        {
            if (page >= SramFirstPage)
            {
                if (!SramReady || SramProtected)
                {
                    return false;
                }

                data = Sram.AsSpan((page - SramFirstPage) * 4, 4).ToArray();
                return true;
            }

            if (page is PwdPage or PackPage)
            {
                data = new byte[4];
                return true;
            }

            if (page == SessionPage0)
            {
                data = GetPage(0, SessionPage0);
                data[0] = SramMirrored ? (byte)(data[0] | NcPassThroughOn) : (byte)(data[0] & ~NcPassThroughOn);
                if (RequireLockOnRequest && SramMirrored)
                    data[0] = _processing || _responseReady ? (byte)(data[0] & 0xFE) : (byte)(data[0] | 0x01);
                return true;
            }

            if (page == SessionPage1)
            {
                var ready = SramReady;
                SessionReads++;
                data = GetPage(0, SessionPage1);
                data[2] = (byte)(data[2] & ~(NsSramRfReady | NsSramI2cReady | NsI2cLocked));
                if (ready) data[2] |= NsSramRfReady;
                if (RequireLockOnRequest && _processing)
                    data[2] = (byte)((data[2] & ~0x20) | NsI2cLocked | NsSramI2cReady);
                return true;
            }
        }

        if ((_access & AccessNfcProt) != 0 && IsProtected(page))
        {
            return false;
        }

        data = GetPage(_sector, page);
        return true;
    }

    private TagResponse PasswordAuth(ReadOnlySpan<byte> password)
    {
        if (AuthLocked)
        {
            return TagResponse.Nak(TagResponse.NakAuthCounterOverflow);
        }

        if (!password.SequenceEqual(_sectors[0].AsSpan(PwdPage * 4, 4)))
        {
            FailedAuthAttempts++;
            return TagResponse.Nak(AuthLocked ? TagResponse.NakAuthCounterOverflow : TagResponse.NakInvalidArgument);
        }

        FailedAuthAttempts = 0;
        _authenticated = true;
        return TagResponse.Bytes(_sectors[0].AsSpan(PackPage * 4, 2).ToArray());
    }

    private TagResponse Write(int page, ReadOnlySpan<byte> data)
    {
        if (_sector == 0 && page >= SramFirstPage)
        {
            if (!SramMirrored || SramProtected) return TagResponse.Nak(TagResponse.NakInvalidArgument);
            if (page == SramFirstPage) _requestPages = 0;
            data.CopyTo(_request.AsSpan((page - SramFirstPage) * 4, 4));
            _requestPages |= (ushort)(1 << (page - SramFirstPage));
            if (page == SramLastPage)
            {
                if (_requestPages == ushort.MaxValue) return AcceptRequest();
                _requestPages = 0;
            }
            return TagResponse.Ack;
        }
        var memory = _sectors[_sector];
        var readOnly = _sector == 0 && (page < StaticLockPage || page >= 0xEA);
        if (readOnly || IsProtected(page) || IsPageLocked(_sector, page))
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        var offset = page * 4;
        if (_sector == 0)
        {
            switch (page)
            {
                case StaticLockPage:
                    WriteStaticLockBytes(data[2], data[3]);
                    return TagResponse.Ack;

                case CcPage:
                    for (var i = 0; i < 4; i++)
                    {
                        memory[offset + i] |= data[i]; // OTP.
                    }

                    return TagResponse.Ack;

                case DynamicLockPage:
                    for (var i = 0; i < 3; i++)
                    {
                        memory[offset + i] |= data[i]; // OTP, byte 3 RFU.
                    }

                    return TagResponse.Ack;

                case PackPage:
                    memory[offset] = data[0];
                    memory[offset + 1] = data[1];
                    return TagResponse.Ack;

                case ConfigPage1:
                    memory[offset] = data[0];
                    memory[offset + 1] = data[1];
                    memory[offset + 2] |= (byte)(data[2] & RegLockNfc); // REG_LOCK_NFC is OTP.
                    return TagResponse.Ack;
            }
        }

        data.CopyTo(memory.AsSpan(offset, 4));
        return TagResponse.Ack;
    }

    private void WriteStaticLockBytes(byte lock0, byte lock1)
    {
        var offset = StaticLockPage * 4;
        var current = _sectors[0][offset + 2];

        // Block-locking bits: BL-CC freezes L-CC, BL9-4 freezes L4-L9, BL15-10 freezes L10-L15.
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

        _sectors[0][offset + 2] |= (byte)(lock0 & ~frozen0);
        _sectors[0][offset + 3] |= (byte)(lock1 & ~frozen1);
    }
}
