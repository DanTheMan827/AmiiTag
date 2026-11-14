namespace AmiiTag.Simulation;

/// <summary>
/// An N2 Elite / Amiiqo as described by the community traces of the official app (YobiWiki "N2 Elite"): an NTAG215
/// (same GET_VERSION) with up to 200 banks of 0x90 pages, managed with vendor commands.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>READ, FAST_READ, WRITE, READ_SIG and GET_VERSION work on the active bank; PWD_AUTH accepts anything (80 80).</item>
/// <item>55 → active bank, bank count, button, firmware. 43 → 16 byte ID.</item>
/// <item>3B start end bank / A5 page bank data read and write any bank; pages 87-8E hold the bank's signature.</item>
/// <item>A9 count, A7 bank, 46 lock, 44 then (button held) 45 unlock: all answer ACK.</item>
/// <item>Locked: the vendor commands other than unlock are ignored (no answer), the rest still works.</item>
/// <item>Unwritten memory reads FF. Lock bits and passwords aren't enforced.</item>
/// </list>
/// </remarks>
public sealed class N2EliteSimulator : TagSimulator
{
    public const int Banks = 200;
    public const int PagesPerBank = 0x90;
    public const int LastPage = 0x8F;

    public static readonly byte[] DefaultId = [0x21, 0x4B, 0x87, 0x02, 0x52, 0x3D, 0x0D, 0x10, 0x16, 0x3A, 0x99, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly byte[] _memory = new byte[Banks * PagesPerBank * 4];
    private bool _unlockPrepared;

    public N2EliteSimulator(int bankCount = 1)
    {
        Array.Fill(_memory, (byte)0xFF);
        BankCount = bankCount;
    }

    /// <summary>
    /// The UID the N2 answers anticollision with (that of the active bank, or a default when it's blank).
    /// </summary>
    public override byte[] Uid7
    {
        get
        {
            var page0 = GetPage(ActiveBank, 0);
            var page1 = GetPage(ActiveBank, 1);
            return page0[0] == 0xFF
                ? [0x04, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66]
                : [page0[0], page0[1], page0[2], page1[0], page1[1], page1[2], page1[3]];
        }
    }

    public int ActiveBank { get; set; }

    public int BankCount { get; set; }

    public bool IsLocked { get; set; }

    /// <summary>
    /// Whether the user is holding the button (needed by the unlock command).
    /// </summary>
    public bool ButtonPressed { get; set; }

    public byte FirmwareVersion { get; set; } = 0x03;

    /// <summary>
    /// Older units answer N2_GET_INFO with two bytes only.
    /// </summary>
    public bool ShortInfo { get; set; }

    public byte[] Id { get; set; } = DefaultId;

    public byte[] GetPage(int bank, int page) => _memory.AsSpan(Offset(bank, page), 4).ToArray();

    /// <summary>
    /// The 540 byte image in a bank.
    /// </summary>
    public byte[] GetBankImage(int bank) => _memory.AsSpan(Offset(bank, 0), 540).ToArray();

    public byte[] GetBankSignature(int bank) => _memory.AsSpan(Offset(bank, 0x87), 32).ToArray();

    public void SetBankImage(int bank, ReadOnlySpan<byte> image) => image.CopyTo(_memory.AsSpan(Offset(bank, 0)));

    private static int Offset(int bank, int page) => (bank * PagesPerBank + page) * 4;

    protected override TagResponse Execute(ReadOnlySpan<byte> command)
    {
        switch (command[0])
        {
            case 0x60:
                return TagResponse.Bytes(Ntag215Simulator.VersionNtag215);

            case 0x30 when command.Length == 2:
                return ReadPages(ActiveBank, command[1], command[1] + 3, wrap: true);

            case 0x3A when command.Length == 3:
                return ReadPages(ActiveBank, command[1], command[2], wrap: false);

            case 0xA2 when command.Length == 6:
                return WritePage(ActiveBank, command[1], command[2..6]);

            case 0x3C:
                return TagResponse.Bytes(GetBankSignature(ActiveBank));

            case 0x1B when command.Length == 5:
                return TagResponse.Bytes([0x80, 0x80]);
        }

        if (IsLocked && command[0] is 0x55 or 0x43 or 0x3B or 0xA5 or 0xA9 or 0xA7 or 0x46)
        {
            // Locked: the vendor commands go unanswered.
            return TagResponse.None;
        }

        switch (command[0])
        {
            case 0x55 when command.Length == 1:
                return TagResponse.Bytes(ShortInfo
                    ? [(byte)ActiveBank, (byte)BankCount]
                    : [(byte)ActiveBank, (byte)BankCount, (byte)(ButtonPressed ? 1 : 0), FirmwareVersion]);

            case 0x43 when command.Length == 1:
                return TagResponse.Bytes((byte[])Id.Clone());

            case 0x3B when command.Length == 4:
                return command[3] >= Banks ? TagResponse.Nak(TagResponse.NakInvalidArgument) : ReadPages(command[3], command[1], command[2], wrap: false);

            case 0xA5 when command.Length == 7:
                return command[2] >= Banks ? TagResponse.Nak(TagResponse.NakInvalidArgument) : WritePage(command[2], command[1], command[3..7]);

            case 0xA9 when command.Length == 2:
                if (command[1] is 0 or > Banks)
                {
                    return TagResponse.Nak(TagResponse.NakInvalidArgument);
                }

                BankCount = command[1];
                if (ActiveBank >= BankCount)
                {
                    ActiveBank = 0;
                }

                return TagResponse.Ack;

            case 0xA7 when command.Length == 2:
                if (command[1] >= Banks)
                {
                    return TagResponse.Nak(TagResponse.NakInvalidArgument);
                }

                ActiveBank = command[1];
                return TagResponse.Ack;

            case 0x46 when command.Length == 1:
                IsLocked = true;
                return TagResponse.Ack;

            case 0x44 when command.Length == 1:
                _unlockPrepared = true;
                return TagResponse.Ack;

            case 0x45 when command.Length == 1:
                if (!_unlockPrepared || !ButtonPressed)
                {
                    return TagResponse.Nak(TagResponse.NakInvalidArgument);
                }

                _unlockPrepared = false;
                IsLocked = false;
                return TagResponse.Ack;
        }

        return TagResponse.Nak(TagResponse.NakInvalidArgument);
    }

    protected override void OnHalted() => _unlockPrepared = false;

    private TagResponse ReadPages(int bank, int start, int end, bool wrap)
    {
        if (start > LastPage || (!wrap && (end > LastPage || end < start)))
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        var output = new byte[(end - start + 1) * 4];
        for (var i = 0; i <= end - start; i++)
        {
            var page = (start + i) % PagesPerBank;
            GetPage(bank, page).CopyTo(output, i * 4);
        }

        return TagResponse.Bytes(output);
    }

    private TagResponse WritePage(int bank, int page, ReadOnlySpan<byte> data)
    {
        if (page > LastPage)
        {
            return TagResponse.Nak(TagResponse.NakInvalidArgument);
        }

        data.CopyTo(_memory.AsSpan(Offset(bank, page), 4));
        return TagResponse.Ack;
    }
}
