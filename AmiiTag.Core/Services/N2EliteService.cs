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
    /// Rereads the N2's settings and the start of every bank (an N2 that's already unlocked).
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

    /// <summary>
    /// What Manage N2 Elite does: forgets any N2 read before, then reads the tag on the reader as an unlocked N2. If it
    /// doesn't answer as one (a locked N2 answers like a plain NTAG215), goes through the unlock flow and reads it again.
    /// Only a tag that still isn't an N2 after that is reported as not found.
    /// </summary>
    public async Task<bool> ScanAsync()
    {
        Forget();

        var read = await TryReadAsync();
        if (read == null)
        {
            // Cancelled, or an NFC error that's already been shown.
            return false;
        }

        if (read.Info == null)
        {
            if (await UnlockCoreAsync() == null)
            {
                return false;
            }

            read = await TryReadAsync();
            if (read == null)
            {
                return false;
            }

            if (read.Info == null)
            {
                await _dialogs.AlertAsync(
                    "No N2 Elite found",
                    "The tag didn't answer as an N2 Elite, even after unlocking. Check it's an N2 Elite or Amiiqo, and keep it still on the reader.");
                return false;
            }
        }

        Info = read.Info;
        Banks = read.Banks;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private sealed record N2Read(N2EliteInfo? Info, IReadOnlyList<N2EliteBank> Banks);

    /// <summary>
    /// One read of the tag on the reader: null when cancelled or failed (the error already shown); no Info when the tag
    /// isn't an unlocked N2.
    /// </summary>
    private Task<N2Read?> TryReadAsync() =>
        _nfc.RunAsync<N2Read?>(HoldPrompt, async (connection, progress, ct) =>
        {
            var info = await N2EliteClient.DetectAsync(connection, ct);
            if (info == null)
            {
                return new N2Read(null, []);
            }

            var banks = await new N2EliteClient(connection).ReadBankSummariesAsync(info.UsableBanks, progress, ct);
            connection.SetStatus("N2 Elite read");
            return new N2Read(info, banks);
        });

    /// <summary>
    /// The unlock flow: the user holds the N2's button while the unlock commands are sent. Null when they back out (or
    /// the reader couldn't connect, already shown); otherwise whether the commands went through. A failure there isn't
    /// reported, since the read after it says whether there's an N2.
    /// </summary>
    private async Task<bool?> UnlockCoreAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Unlock N2 Elite",
                "The tag didn't answer as an unlocked N2 Elite, so it may be locked. Keep it on the reader, then press and hold its button. Keep holding it until unlocking is done.",
                "I'm Holding It", "Cancel"))
        {
            return null;
        }

        var unlocked = await _nfc.RunAsync<bool?>("Keep holding the button...", async (connection, _, ct) =>
        {
            try
            {
                await new N2EliteClient(connection).UnlockAsync(token => Task.Delay(TimeSpan.FromSeconds(3), token), ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"N2 Elite: unlock failed: {ex.Message}");
                return false;
            }
        });

        if (unlocked == true)
        {
            await _dialogs.AlertAsync("Unlocked", "You can let go of the button.");
        }

        return unlocked;
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
    /// Writes a tag to an N2 Elite from elsewhere in the app: scans the N2 (unlocking it if needed) to list its banks, then
    /// writes the chosen one.
    /// </summary>
    public async Task WriteToChosenBankAsync(TagDump dump)
    {
        if (!await ScanAsync())
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
        ?? throw new TagException("No N2 Elite answered. Hold the N2 Elite still on the reader, or scan it again from Manage N2 Elite if it's been locked.");

    private static string SafeFileName(string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Trim();
        return string.IsNullOrEmpty(safe) ? "tag" : safe;
    }
}
