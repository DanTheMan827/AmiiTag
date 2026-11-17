namespace AmiiTag.Simulation;

/// <summary>
/// Behavioral N2 Elite model. The host supplies button state and actual RF-field
/// boundaries; HALT/reselection is not a field boundary. Commands exclude CRC.
/// </summary>
public sealed class N2EliteSimulator : TagSimulator
{
    public const int Banks = 200;
    public const int PagesPerBank = 0x90;
    public const int LastPage = PagesPerBank - 1;

    private const int StandardPages = 135;
    private const int BankBytes = PagesPerBank * 4;

    // Return a fresh array so callers cannot modify shared defaults.
    public static byte[] DefaultId =>
    [
        0x21, 0x4B, 0x87, 0x02, 0x52, 0x3D, 0x0D, 0x10,
        0x16, 0x3A, 0x99, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
    ];

    private readonly byte[] _memory = new byte[Banks * BankBytes];
    private byte[] _id = DefaultId;

    private int _activeBank;
    private int _bankCount;
    private bool _locked;
    private bool _unlockPrepared;
    private bool _fieldPresent;
    private bool _sawFastRead;
    private bool _cycleReady;

    public N2EliteSimulator(int bankCount = 1)
    {
        BankCount = bankCount;
        Array.Fill(_memory, (byte)0xFF);
    }

    public override byte[] Uid7
    {
        get
        {
            int p = _activeBank * BankBytes;

            // FF is stored data, not a sentinel for a synthetic UID.
            return
            [
                _memory[p],
                _memory[p + 1],
                _memory[p + 2],
                _memory[p + 4],
                _memory[p + 5],
                _memory[p + 6],
                _memory[p + 7]
            ];
        }
    }

    public int ActiveBank
    {
        get => _activeBank;
        set
        {
            ValidateBank(value);
            _activeBank = value;
        }
    }

    public int BankCount
    {
        get => _bankCount;
        set
        {
            if (value is < 1 or > Banks)
                throw new ArgumentOutOfRangeException(nameof(value));

            _bankCount = value;
        }
    }

    public bool IsLocked
    {
        get => _locked;
        set
        {
            _locked = value;
            _unlockPrepared = false;
        }
    }

    public bool ButtonPressed
    {
        get; set;
    }

    public byte FirmwareVersion { get; set; } = 0x03;

    public bool ShortInfo
    {
        get; set;
    }

    public byte[] Id
    {
        get => (byte[])_id.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Length != 16)
            {
                throw new ArgumentException(
                    "An N2 ID is 16 bytes.",
                    nameof(value));
            }

            _id = (byte[])value.Clone();
        }
    }

    public byte[] GetPage(int bank, int page) =>
        _memory.AsSpan(Offset(bank, page), 4).ToArray();

    public byte[] GetBankImage(int bank) =>
        _memory.AsSpan(Offset(bank, 0), 540).ToArray();

    public byte[] GetBankSignature(int bank) =>
        _memory.AsSpan(Offset(bank, 0x87), 32).ToArray();

    /// <summary>
    /// Load 540 bytes (tag), 572 (tag and signature), or 576 (whole bank).
    /// Bytes outside the supplied image remain unchanged.
    /// </summary>
    public void SetBankImage(int bank, ReadOnlySpan<byte> image)
    {
        int offset = Offset(bank, 0);

        if (image.Length is not (540 or 572 or 576))
        {
            throw new ArgumentException(
                "Expected 540, 572 or 576 bytes.",
                nameof(image));
        }

        image.CopyTo(_memory.AsSpan(offset, BankBytes));
    }

    /// <summary>
    /// Call on RF-field acquisition, before obtaining Uid7.
    /// A held button cycles once if the preceding field session contained
    /// a valid FAST_READ.
    /// </summary>
    public void BeginFieldSession()
    {
        if (_fieldPresent)
            return;

        if (_cycleReady && ButtonPressed && BankCount > 1)
            ActiveBank = (ActiveBank + 1) % BankCount;

        _fieldPresent = true;
        _cycleReady = false;
        _sawFastRead = false;
        _unlockPrepared = false;
    }

    /// <summary>
    /// Call on actual RF-field loss, including while halted.
    /// </summary>
    public void EndFieldSession()
    {
        _unlockPrepared = false;

        if (!_fieldPresent)
            return;

        _cycleReady = _sawFastRead;
        _fieldPresent = false;
        _sawFastRead = false;
    }

    // HALT is not removal of the RF field.
    protected override void OnHalted()
    {
    }

    protected override TagResponse Execute(ReadOnlySpan<byte> command)
    {
        if (command.IsEmpty)
            return Invalid();

        switch (command[0])
        {
            case 0x60 when command.Length == 1:
                return TagResponse.Bytes(
                    [0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x11, 0x03]);

            case 0x30 when command.Length == 2:
                return ReadStandard(
                    command[1],
                    command[1] + 3,
                    wrap: true);

            case 0x3A when command.Length == 3:
                {
                    if (command[1] > command[2] ||
                        command[2] >= StandardPages)
                    {
                        return Invalid();
                    }

                    _sawFastRead = true;

                    return ReadStandard(
                        command[1],
                        command[2],
                        wrap: false);
                }

            case 0xA2 when command.Length == 6:
                if (command[1] >= StandardPages)
                    return Invalid();

                Write(ActiveBank, command[1], command[2..]);

                // Standard Type 2 four-bit ACK.
                return TagResponse.Ack;

            case 0x3C when command.Length == 1 ||
                              (command.Length == 2 && command[1] == 0):
                return TagResponse.Bytes(
                    GetBankSignature(ActiveBank));

            case 0x1B when command.Length == 5:
                return TagResponse.Bytes([0x80, 0x80]);
        }

        if (IsLocked && command[0] is
            0x55 or 0x43 or 0x3B or 0xA5 or
            0xAE or 0xA9 or 0xA7 or 0x46)
        {
            return TagResponse.None;
        }

        switch (command[0])
        {
            case 0x55 when command.Length == 1:
                return TagResponse.Bytes(
                    ShortInfo
                        ? [(byte)ActiveBank, (byte)BankCount]
                        :
                        [
                            (byte)ActiveBank,
                            (byte)BankCount,
                            (byte)(ButtonPressed ? 1 : 0),
                            FirmwareVersion
                        ]);

            case 0x43 when command.Length == 1:
                return TagResponse.Bytes(Id);

            case 0x3B when command.Length == 4:
                {
                    int bank = command[3];
                    int start = command[1];
                    int end = command[2];

                    if (bank >= Banks || end < start)
                        return Invalid();

                    // N2 vendor reads are linear and can spill into
                    // the following bank.
                    int offset = bank * BankBytes + start * 4;
                    int length = (end - start + 1) * 4;

                    if (offset > _memory.Length - length)
                        return Invalid();

                    return TagResponse.Bytes(
                        _memory.AsSpan(offset, length).ToArray());
                }

            case 0xA5 when command.Length == 7:
                if (command[2] >= Banks || command[1] > LastPage)
                    return Invalid();

                Write(command[2], command[1], command[3..]);
                return VendorAck();

            case 0xAE when command.Length >= 4:
                {
                    int page = command[1];
                    int bank = command[2];
                    int size = command[3];

                    if (bank >= Banks ||
                        page > LastPage ||
                        size == 0 ||
                        size % 4 != 0 ||
                        command.Length != 4 + size ||
                        page * 4 + size > BankBytes)
                    {
                        return Invalid();
                    }

                    Write(bank, page, command[4..]);
                    return VendorAck();
                }

            case 0xA9 when command.Length == 2:
                if (command[1] is 0 or > Banks)
                    return Invalid();

                BankCount = command[1];
                return VendorAck();

            case 0xA7 when command.Length == 2:
                if (command[1] >= Banks)
                    return Invalid();

                ActiveBank = command[1];
                return VendorAck();

            case 0x46 when command.Length == 1:
                IsLocked = true;
                return VendorAck();

            case 0x44 when command.Length == 1:
                _unlockPrepared = true;
                return TagResponse.None;

            case 0x45 when command.Length == 1:
                {
                    bool allowed = _unlockPrepared && ButtonPressed;
                    _unlockPrepared = false;

                    if (!allowed)
                        return TagResponse.None;

                    IsLocked = false;
                    return VendorAck();
                }

            default:
                return Invalid();
        }
    }

    private TagResponse ReadStandard(
        int start,
        int end,
        bool wrap)
    {
        if (start >= StandardPages ||
            (!wrap && (end < start || end >= StandardPages)))
        {
            return Invalid();
        }

        var output = new byte[(end - start + 1) * 4];

        for (int i = 0; i <= end - start; i++)
        {
            int page = (start + i) % StandardPages;
            int offset = ActiveBank * BankBytes + page * 4;

            _memory.AsSpan(offset, 4)
                .CopyTo(output.AsSpan(i * 4, 4));
        }

        return TagResponse.Bytes(output);
    }

    private void Write(
        int bank,
        int page,
        ReadOnlySpan<byte> data)
    {
        data.CopyTo(
            _memory.AsSpan(
                bank * BankBytes + page * 4,
                data.Length));
    }

    private static TagResponse VendorAck() =>
        TagResponse.Bytes([0x0A]);

    private static TagResponse Invalid() =>
        TagResponse.Nak(TagResponse.NakInvalidArgument);

    private static void ValidateBank(int bank)
    {
        if ((uint)bank >= Banks)
            throw new ArgumentOutOfRangeException(nameof(bank));
    }

    private static int Offset(int bank, int page)
    {
        ValidateBank(bank);

        if ((uint)page >= PagesPerBank)
            throw new ArgumentOutOfRangeException(nameof(page));

        return bank * BankBytes + page * 4;
    }
}