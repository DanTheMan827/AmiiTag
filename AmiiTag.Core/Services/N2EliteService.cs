using AmiiTag.Core.Interfaces;
using AmiiTag.Nfc;
using AmiiTag.Tags;
using System.IO.Compression;

namespace AmiiTag.Core.Services;

/// <summary>
/// The N2 Elite / Amiiqo flows: each one is a single NFC session (the user holds the N2 on the reader), checked to be
/// the same N2 the screen is showing.
/// </summary>
public sealed class N2EliteService
{
    private readonly NfcService _nfc;
    private readonly IDialogService _dialogs;
    private readonly IFileService _files;
    private readonly AmiiboLookup _lookup;

    public N2EliteService(NfcService nfc, IDialogService dialogs, IFileService files, AmiiboLookup lookup)
    {
        _nfc = nfc;
        _dialogs = dialogs;
        _files = files;
        _lookup = lookup;
    }

    /// <summary>
    /// The N2 last read, and its banks.
    /// </summary>
    public N2EliteInfo? Info
    {
        get; private set;
    }

    public IReadOnlyList<N2EliteBank> Banks { get; private set; } = [];

    public event EventHandler? Changed;

    private const string HoldPrompt = "Hold the N2 Elite near the reader.";

    private static class InnerMethods
    {
        public static async Task<(N2EliteInfo info, IReadOnlyList<N2EliteBank> banks)> RefreshAsync(INfcTagConnection connection, IProgress<double> progress, CancellationToken ct)
        {
            var client = new N2EliteClient(connection);
            var info = await DetectOrThrowAsync(connection, ct);
            var banks = await client.ReadBankSummariesAsync(info.UsableBanks, progress, ct);
            return (info, banks);
        }
    }



    /// <summary>
    /// Reads the N2's settings and the start of every bank.
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        var result = await _nfc.RunAsync(HoldPrompt, InnerMethods.RefreshAsync, "N2 Elite read");

        if (result.info == null)
        {
            return false;
        }

        Info = result.info;
        Banks = result.banks;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Forget()
    {
        Info = null;
        Banks = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<TagDump?> ReadBankAsync(int bank) =>
        await RunOnSameN2Async($"Reading bank {bank + 1}", (client, progress, ct) => client.ReadBankAsync(bank, progress, ct));

    public async Task<bool> WriteBankAsync(int bank, TagDump dump)
    {
        var validation = _lookup.Validate(dump);
        if (validation.Status is not (AmiiboValidity.Valid or AmiiboValidity.Unverified) &&
            !await _dialogs.ConfirmAsync(
                "Not a working amiibo",
                $"{string.Join("\n", validation.Problems)}\n\nWrite it to bank {bank + 1} anyway?",
                "Write", "Cancel"))
        {
            return false;
        }

        var ok = await RunOnSameN2Async($"Writing bank {bank + 1}", async (client, progress, ct) =>
        {
            await client.WriteBankAsync(bank, dump, progress, ct);
            return true;
        }, "Bank written");

        if (ok)
        {
            await UpdateBankAsync(bank, dump);
        }

        return ok;
    }

    public async Task<bool> WipeBankAsync(int bank)
    {
        var ok = await RunOnSameN2Async($"Wiping bank {bank + 1}", async (client, progress, ct) =>
        {
            await client.WipeBankAsync(bank, progress, ct);
            return true;
        }, "Bank wiped");

        if (ok)
        {
            var blank = new byte[TagDump.DataLength];
            Array.Fill(blank, (byte)0xFF);
            await UpdateBankAsync(bank, new TagDump(blank));
        }

        return ok;
    }

    public async Task<bool> WipeBanksAfterAsync(int startBank = -1)
    {
        if (startBank >= N2EliteClient.MaxBanks)
        {
            throw new ArgumentException($"Bank {startBank + 1} is out of range: the N2 Elite has {N2EliteClient.MaxBanks} banks.", nameof(startBank));
        }

        var ok = await RunOnSameN2Async(startBank < 0 ? "Wiping all banks" : $"Wiping banks after {startBank + 1}", async (client, progress, ct) =>
        {
            await client.WipeBanksAfter(startBank, progress, ct);
            return true;
        }, "Banks wiped");

        if (ok)
        {
            for (var bank = startBank; bank < Banks.Count; bank++)
            {
                var blank = new byte[TagDump.DataLength];
                Array.Fill(blank, (byte)0xFF);
                await UpdateBankAsync(bank + 1, new TagDump(blank));
            }
        }

        return ok;
    }

    public async Task<bool> SelectBankAsync(int bank)
    {
        var ok = await RunOnSameN2Async($"Activating bank {bank + 1}", async (client, _, ct) =>
        {
            await client.SelectBankAsync(bank, ct);
            return true;
        }, $"Bank {bank + 1} active");

        if (ok && Info != null)
        {
            Info = Info with
            {
                ActiveBank = bank
            };
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return ok;
    }

    /// <summary>
    /// Changes how many banks are enabled, then rereads the list.
    /// </summary>
    public async Task<bool> SetBankCountAsync(int count)
    {
        var ok = await RunOnSameN2Async("Changing the bank count", async (client, progress, ct) =>
        {
            var info = await DetectOrThrowAsync(client.Connection, ct);

            await client.SetBankCountAsync(count, ct);

            if (info.ActiveBank > count)
            {
                await client.SelectBankAsync(count - 1);
            }

            var result = await InnerMethods.RefreshAsync(client.Connection, progress, ct);

            if (result.info == null)
            {
                return false;
            }

            Info = result.info;
            Banks = result.banks;
            Changed?.Invoke(this, EventArgs.Empty);

            return true;
        }, "Bank count changed");

        return ok;
    }

    public async Task<bool> LockAsync()
    {
        var ok = await RunOnSameN2Async("Locking", async (client, _, ct) =>
        {
            await client.LockAsync(ct);
            return true;
        }, "N2 Elite locked");

        if (ok)
        {
            Forget();
        }

        return ok;
    }

    /// <summary>
    /// Unlocks a locked N2: the user holds its button while the unlock commands are sent. A locked N2 answers like a
    /// plain NTAG215, so this doesn't check which N2 it is.
    /// </summary>
    public async Task<bool> UnlockAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Unlock N2 Elite",
                "Put the N2 Elite on the reader, then press and hold its button. Keep holding it, and keep it on the reader, until unlocking is done.",
                "I'm Holding It", "Cancel"))
        {
            return false;
        }

        var ok = await _nfc.RunAsync("Keep holding the button...", async (connection, _, ct) =>
        {
            await new N2EliteClient(connection).UnlockAsync(token => Task.Delay(TimeSpan.FromSeconds(3), token), ct);
            return true;
        }, "N2 Elite unlocked");

        if (ok)
        {
            await _dialogs.AlertAsync("Unlocked", "You can let go of the button.");
            await RefreshAsync();
        }

        return ok;
    }

    /// <summary>
    /// Reads every enabled bank and saves them as a zip of .bin files.
    /// </summary>
    public async Task BackupAllAsync()
    {
        var info = Info;
        if (info == null)
        {
            return;
        }

        var dumps = await RunOnSameN2Async("Backing up every bank", async (client, progress, ct) =>
        {
            var list = new List<(int Bank, TagDump Dump)>();
            for (var bank = 0; bank < info.UsableBanks; bank++)
            {
                var index = bank;
                var bankProgress = new Progress<double>(f => progress.Report((index + f) / info.UsableBanks));
                list.Add((bank, await client.ReadBankAsync(bank, bankProgress, ct)));
            }

            return list;
        }, "Backup read");

        if (dumps == null)
        {
            return;
        }

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (bank, dump) in dumps)
            {
                var empty = dump.Span[..TagDump.DataLength].IndexOfAnyExcept((byte)0xFF) < 0;
                var name = empty ? "empty" : SafeFileName(_lookup.GetDisplayName(dump));
                var entry = zip.CreateEntry($"Bank {bank + 1:000} - {name}.bin", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await stream.WriteAsync(dump.Data);
            }
        }

        await _files.SaveFileAsync($"N2 Elite {info.IdHex}.zip", buffer.ToArray(), "application/zip");
    }

    /// <summary>
    /// Writes a tag to an N2 Elite from elsewhere in the app: reads the N2 to list its banks, then writes the chosen one.
    /// </summary>
    public async Task WriteToChosenBankAsync(TagDump dump)
    {
        if (!await RefreshAsync())
        {
            return;
        }

        var options = Banks.Select(b => $"{b.Bank + 1}. {(b.IsEmpty ? "Empty" : _lookup.GetSlotDisplayName(b.Summary))}{(b.Bank == Info?.ActiveBank ? " (active)" : string.Empty)}").ToList();
        var choice = await _dialogs.ChooseAsync("Write to Bank", options);
        if (choice >= 0 && await WriteBankAsync(Banks[choice].Bank, dump))
        {
            await _dialogs.AlertAsync("Done", $"Written to bank {Banks[choice].Bank + 1}.");
        }
    }

    private Task UpdateBankAsync(int bank, TagDump dump)
    {
        var summary = new byte[TagDump.DataLength];
        dump.Span[..(N2EliteClient.SummaryPages * 4)].CopyTo(summary);
        Banks = Banks.Select(b => b.Bank == bank ? new N2EliteBank(bank, new TagDump(summary)) : b).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs an operation on the N2 on the reader, after checking it's the one on screen (when one is).
    /// </summary>
    private async Task<T?> RunOnSameN2Async<T>(string what, Func<N2EliteClient, IProgress<double>, CancellationToken, Task<T>> operation, string? success = null)
    {
        var expected = Info;
        return await _nfc.RunAsync($"{what}. {HoldPrompt}", async (connection, progress, ct) =>
        {
            var info = await DetectOrThrowAsync(connection, ct);
            if (expected != null && expected.Id.Length > 0 && !info.Id.AsSpan().SequenceEqual(expected.Id))
            {
                throw new TagException("This is a different N2 Elite from the one shown. Refresh to manage it.");
            }

            return await operation(new N2EliteClient(connection), progress, ct);
        }, success);
    }

    private static async Task<N2EliteInfo> DetectOrThrowAsync(INfcTagConnection connection, CancellationToken ct) =>
        await N2EliteClient.DetectAsync(connection, ct)
        ?? throw new TagException("No N2 Elite answered. If it's locked it answers like a normal tag: unlock it first.");

    private static string SafeFileName(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
        return string.IsNullOrEmpty(safe) ? "tag" : safe;
    }
}
