using AmiiTag.Tags;

namespace AmiiTag.Nfc;

/// <summary>
/// What an N2 Elite / Amiiqo reports about itself (N2_GET_INFO and N2_GET_ID).
/// </summary>
/// <param name="ActiveBank">The bank the console sees, 0 based.</param>
/// <param name="BankCount">How many banks are enabled (1-200).</param>
/// <param name="ButtonPressed">True while the button is held.</param>
/// <param name="FirmwareVersion">The firmware version byte, if the device reported one (older units don't).</param>
/// <param name="Id">The 16 bytes of N2_GET_ID, or empty.</param>
public sealed record N2EliteInfo(int ActiveBank, int BankCount, bool ButtonPressed, int? FirmwareVersion, byte[] Id)
{
    /// <summary>
    /// The device ID as the official app shows it: the first 11 bytes of N2_GET_ID.
    /// </summary>
    public string IdHex => Id.Length >= 11 ? Hex.ToHex(Id.AsSpan(0, 11)) : Hex.ToHex(Id);

    /// <summary>
    /// The ID a Pixl.js answers with when its tag emulator plays an N2 Elite (copied from the YobiWiki traces).
    /// </summary>
    public static readonly byte[] PixlEmulatorId = [0x21, 0x4B, 0x87, 0x02, 0x52, 0x3D, 0x0D, 0x10, 0x16, 0x3A, 0x24, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>
    /// True for a Pixl.js answering the N2 commands (ntag_emu_v2.c): it reports a fixed 50 banks on firmware 3 but
    /// holds a single tag, ignoring the bank in reads and writes and treating bank selection as a no-op.
    /// </summary>
    public bool IsSingleTagEmulator =>
        Id.AsSpan().SequenceEqual(PixlEmulatorId) && BankCount == 0x32 && ActiveBank == 0 && FirmwareVersion == 3;

    /// <summary>
    /// The banks that really hold separate tags.
    /// </summary>
    public int UsableBanks => IsSingleTagEmulator ? 1 : BankCount;
}

/// <summary>
/// One bank as listed: the first 32 pages (UID, ID block, settings and salt), enough to show a name and image.
/// </summary>
public sealed record N2EliteBank(int Bank, TagDump Summary)
{
    /// <summary>
    /// Unwritten banks read back as FF.
    /// </summary>
    public bool IsEmpty => Summary.Span[..(N2EliteClient.SummaryPages * 4)].IndexOfAnyExcept((byte)0xFF) < 0;
}

/// <summary>
/// Talks to an N2 Elite (Amiiqo), an NTAG215 emulator with up to 200 banks, through its vendor NFC commands.
/// </summary>
/// <remarks>
/// Reverse engineered by the community from the official N2 app (see YobiWiki "N2 Elite"):
/// <code>
/// 55                       N2_GET_INFO    → active bank, bank count, button, firmware (older units: 2 bytes)
/// 43                       N2_GET_ID      → 16 bytes, the first 11 are the device ID
/// 3B start end bank        N2_FAST_READ   → like FAST_READ (3A), on any bank
/// A5 page bank d0 d1 d2 d3 N2_WRITE       → like WRITE (A2), on any bank → ACK
/// A9 count                 N2_SET_BANK_CNT → ACK
/// A7 bank                  N2_SELECT_BANK → ACK (makes it the active bank)
/// 46                       N2_LOCK        → ACK; the vendor commands stop answering until unlocked
/// 44 … hold button … 45    N2_UNLOCK      → ACK
/// </code>
/// To everything else the device is an ordinary NTAG215 (same GET_VERSION), so it's found by asking N2_GET_INFO: a
/// real NTAG215 NAKs it (and halts until reselected). Each bank is a 540 byte NTAG215 image (pages 00-86), followed by
/// its 32 byte originality signature in pages 87-8E. Blank banks read as FF.
/// </remarks>
public sealed class N2EliteClient
{
    public const byte CmdGetInfo = 0x55;
    public const byte CmdGetId = 0x43;
    public const byte CmdFastRead = 0x3B;
    public const byte CmdWrite = 0xA5;
    public const byte CmdSetBankCount = 0xA9;
    public const byte CmdSelectBank = 0xA7;
    public const byte CmdLock = 0x46;
    public const byte CmdPrepareUnlock = 0x44;
    public const byte CmdUnlock = 0x45;

    public const int MaxBanks = 200;
    public const int BankPages = TagDump.PageCount;
    public const int SignatureFirstPage = 0x87;
    public const int SignaturePages = 8;

    /// <summary>
    /// Pages read for each bank in the list (0x00-0x1F: UID, ID block, settings and keygen salt).
    /// </summary>
    public const int SummaryPages = 0x20;

    // libnfc based readers can't take more than 63 pages (252 bytes) in one answer.
    private const int MaxPagesPerRead = 63;

    private readonly INfcTagConnection _connection;

    public N2EliteClient(INfcTagConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// Asks the tag on the reader whether it's an N2 Elite. Returns null for anything else, including a locked N2
    /// (which answers like a plain NTAG215). The session is unusable after a null result: reconnect first.
    /// </summary>
    public static async Task<N2EliteInfo?> DetectAsync(INfcTagConnection connection, CancellationToken cancellationToken = default)
    {
        if (!connection.IsDirectCommandMode)
        {
            throw new NfcException("This reader can't send the N2 Elite's commands: it isn't in direct command mode.");
        }

        try
        {
            return await new N2EliteClient(connection).GetInfoAsync(cancellationToken);
        }
        catch (NfcCommandNotSupportedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is NfcException or TagException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// N2_GET_INFO followed by N2_GET_ID.
    /// </summary>
    public async Task<N2EliteInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirect();
        var info = await _connection.TransceiveAsync([CmdGetInfo], cancellationToken);
        if (info.Length is < 2 or > 4)
        {
            throw new TagException("This isn't an N2 Elite (or it's locked).");
        }

        var activeBank = info[0];
        var bankCount = info[1];
        if (bankCount is 0 or > MaxBanks || activeBank >= MaxBanks)
        {
            throw new TagException("This isn't an N2 Elite (unexpected N2_GET_INFO answer).");
        }

        var id = Array.Empty<byte>();
        try
        {
            var answer = await _connection.TransceiveAsync([CmdGetId], cancellationToken);
            if (answer.Length >= 11)
            {
                id = answer;
            }
        }
        catch (NfcException)
        {
            // Not essential, and every unit seen so far answers it.
        }

        return new N2EliteInfo(
            activeBank,
            bankCount,
            info.Length >= 3 && info[2] != 0,
            info.Length >= 4 ? info[3] : null,
            id);
    }

    /// <summary>
    /// N2_FAST_READ of <paramref name="startPage"/>..<paramref name="endPage"/> (inclusive) on a bank, split as needed.
    /// </summary>
    public async Task<byte[]> ReadPagesAsync(int bank, int startPage, int endPage, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        CheckBank(bank);
        if (startPage < 0 || endPage > 0xFF || endPage < startPage)
        {
            throw new ArgumentOutOfRangeException(nameof(startPage));
        }

        EnsureDirect();
        var pagesPerRead = Math.Clamp(_connection.MaxFastReadPages, 1, MaxPagesPerRead);
        var total = endPage - startPage + 1;
        var output = new byte[total * 4];
        for (var page = startPage; page <= endPage; page += pagesPerRead)
        {
            var last = Math.Min(endPage, page + pagesPerRead - 1);
            var expected = (last - page + 1) * 4;
            var data = await _connection.TransceiveAsync([CmdFastRead, (byte)page, (byte)last, (byte)bank], cancellationToken);
            if (data.Length < expected)
            {
                throw new TagException(data.Length == 1
                    ? $"The N2 Elite refused to read bank {bank + 1} (NAK 0x{data[0]:X}). Is it locked?"
                    : $"Short read from bank {bank + 1} (pages 0x{page:X2}-0x{last:X2}).");
            }

            Array.Copy(data, 0, output, (page - startPage) * 4, expected);
            progress?.Report((double)(last - startPage + 1) / total);
        }

        return output;
    }

    /// <summary>
    /// Reads a whole bank (540 bytes), with its signature if one has been stored.
    /// </summary>
    public async Task<TagDump> ReadBankAsync(int bank, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var data = await ReadPagesAsync(bank, 0, SignatureFirstPage + SignaturePages - 1, progress, cancellationToken);
        var signature = data.AsSpan(TagDump.DataLength, TagDump.SignatureLength);

        // Unset signatures read as FF (or 00 on some units): leave them out rather than saving a fake one.
        if (signature.IndexOfAnyExcept((byte)0xFF) < 0 || signature.IndexOfAnyExcept((byte)0x00) < 0)
        {
            return new TagDump(data.AsSpan(0, TagDump.DataLength));
        }

        return new TagDump(data.AsSpan(0, TagDump.FullLength));
    }

    /// <summary>
    /// Reads the start of every enabled bank, for listing them.
    /// </summary>
    public async Task<IReadOnlyList<N2EliteBank>> ReadBankSummariesAsync(int bankCount, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var banks = new List<N2EliteBank>(bankCount);
        for (var bank = 0; bank < bankCount; bank++)
        {
            var pages = await ReadPagesAsync(bank, 0, SummaryPages - 1, null, cancellationToken);
            var image = new byte[TagDump.DataLength];
            pages.CopyTo(image, 0);
            banks.Add(new N2EliteBank(bank, new TagDump(image)));
            progress?.Report((double)(bank + 1) / bankCount);
        }

        return banks;
    }

    /// <summary>
    /// N2_WRITE of one page on a bank.
    /// </summary>
    public Task WritePageAsync(int bank, int page, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        CheckBank(bank);
        if (data.Length != 4)
        {
            throw new ArgumentException("A page is 4 bytes.", nameof(data));
        }

        byte[] command = [CmdWrite, (byte)page, (byte)bank, data[0], data[1], data[2], data[3]];
        return ExpectAckAsync(command, $"write page 0x{page:X2} of bank {bank + 1}", cancellationToken);
    }

    /// <summary>
    /// Writes a 540 byte image to a bank, page by page in order as the official app does (the N2 doesn't enforce lock
    /// bits or passwords, so nothing has to be held back), followed by its signature when the dump has one.
    /// </summary>
    public async Task WriteBankAsync(int bank, TagDump dump, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        CheckBank(bank);
        EnsureDirect();
        var data = dump.ToArray(TagDump.DataLength);
        var signature = dump.HasSignature ? dump.Signature : null;
        var total = BankPages + (signature != null ? SignaturePages : 0);

        for (var page = 0; page < BankPages; page++)
        {
            await WritePageAsync(bank, page, data.AsSpan(page * 4, 4), cancellationToken);
            progress?.Report((double)(page + 1) / total);
        }

        if (signature != null)
        {
            for (var i = 0; i < SignaturePages; i++)
            {
                await WritePageAsync(bank, SignatureFirstPage + i, signature.AsSpan(i * 4, 4), cancellationToken);
                progress?.Report((double)(BankPages + i + 1) / total);
            }
        }
    }

    /// <summary>
    /// Returns a bank to the unwritten state (FF everywhere, signature included).
    /// </summary>
    public async Task WipeBankAsync(int bank, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        CheckBank(bank);
        EnsureDirect();
        byte[] blank = [0xFF, 0xFF, 0xFF, 0xFF];
        var total = BankPages + SignaturePages;
        for (var page = 0; page < total; page++)
        {
            await WritePageAsync(bank, page, blank, cancellationToken);
            progress?.Report((double)(page + 1) / total);
        }
    }

    /// <summary>
    /// N2_SET_BANK_CNT: how many banks the button cycles through (1-200). Banks past the count keep their data.
    /// </summary>
    public Task SetBankCountAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > MaxBanks)
        {
            throw new ArgumentOutOfRangeException(nameof(count), $"The bank count must be 1-{MaxBanks}.");
        }

        return ExpectAckAsync([CmdSetBankCount, (byte)count], "change the bank count", cancellationToken);
    }

    /// <summary>
    /// N2_SELECT_BANK: makes a bank the one the console sees.
    /// </summary>
    public Task SelectBankAsync(int bank, CancellationToken cancellationToken = default)
    {
        CheckBank(bank);
        return ExpectAckAsync([CmdSelectBank, (byte)bank], $"activate bank {bank + 1}", cancellationToken);
    }

    /// <summary>
    /// N2_LOCK: the N2 then behaves as a plain NTAG215 (the vendor commands stop answering) until it's unlocked.
    /// </summary>
    public Task LockAsync(CancellationToken cancellationToken = default) =>
        ExpectAckAsync([CmdLock], "lock the N2 Elite", cancellationToken);

    /// <summary>
    /// N2_UNLOCK: sends the prepare command, waits for <paramref name="waitForButton"/> (the user holds the N2's button
    /// for a few seconds meanwhile), then sends the unlock command.
    /// </summary>
    public async Task UnlockAsync(Func<CancellationToken, Task> waitForButton, CancellationToken cancellationToken = default)
    {
        EnsureDirect();
        await _connection.TransceiveAsync([CmdPrepareUnlock], cancellationToken);
        await waitForButton(cancellationToken);
        await ExpectAckAsync([CmdUnlock], "unlock the N2 Elite (hold the button until it's done)", cancellationToken);
    }

    private async Task ExpectAckAsync(byte[] command, string what, CancellationToken cancellationToken)
    {
        EnsureDirect();
        var answer = await _connection.TransceiveAsync(command, cancellationToken);
        if (answer is not [0x0A])
        {
            throw new TagException(answer.Length == 1
                ? $"The N2 Elite couldn't {what} (NAK 0x{answer[0]:X})."
                : $"The N2 Elite couldn't {what} (unexpected answer {Hex.ToHex(answer)}).");
        }
    }

    private void EnsureDirect()
    {
        if (!_connection.IsDirectCommandMode)
        {
            throw new NfcException("This reader can't send the N2 Elite's commands: it isn't in direct command mode.");
        }
    }

    private static void CheckBank(int bank)
    {
        if (bank is < 0 or >= MaxBanks)
        {
            throw new ArgumentOutOfRangeException(nameof(bank), $"Banks are numbered 0-{MaxBanks - 1}.");
        }
    }
}
