using AmiiTag.AmiiboData;
using AmiiTag.Bluetooth;
using AmiiTag.Bluetooth.Devices;
using AmiiTag.Core.Interfaces;
using AmiiTag.Nfc;
using AmiiTag.Tags;

namespace AmiiTag.Core.Services;

/// <summary>
/// The user facing flows (scan, write, load, save...) shared by every screen and host.
/// </summary>
public sealed class TagActions
{
    private readonly NfcService _nfc;
    private readonly BleDeviceManager _ble;
    private readonly IDialogService _dialogs;
    private readonly IAppNavigator _navigator;
    private readonly TagStore _tags;
    private readonly KeyStore _keys;
    private readonly SignatureStore _signatures;
    private readonly SettingsStore _settings;
    private readonly AmiiboLookup _lookup;
    private readonly IFileService _files;
    private readonly IQrScanner _qr;
    private readonly DeviceCapabilities _capabilities;

    public TagActions(
        NfcService nfc,
        BleDeviceManager ble,
        IDialogService dialogs,
        IAppNavigator navigator,
        TagStore tags,
        KeyStore keys,
        SignatureStore signatures,
        SettingsStore settings,
        AmiiboLookup lookup,
        IFileService files,
        IQrScanner qr,
        DeviceCapabilities capabilities)
    {
        _capabilities = capabilities;
        _nfc = nfc;
        _ble = ble;
        _dialogs = dialogs;
        _navigator = navigator;
        _tags = tags;
        _keys = keys;
        _signatures = signatures;
        _settings = settings;
        _lookup = lookup;
        _files = files;
        _qr = qr;
    }

    public static readonly IReadOnlyList<string> TagFileExtensions = [".bin", ".nfc", ".ntag", ".dump"];

    /// <param name="automatic">True when no tap led here (the background scan); see <see cref="AppRoutes.Tag"/>.</param>
    public async Task OpenTagAsync(IAmiiboImage dump, bool modal = true, bool automatic = false)
    {
        var key = _tags.Add(dump);
        await _navigator.NavigateAsync(AppRoutes.Tag(key, _lookup.GetDisplayName(dump), modal, automatic));
    }

    // ---------------------------------------------------------------- Reading

    public async Task ScanTagAsync()
    {
        var sources = new List<(string Title, Func<Task> Run)>();
        if (_nfc.IsAvailable)
        {
            sources.Add(("NFC tag", ReadNfcAsync));
        }

        foreach (var device in _ble.Connected.OfType<ISlotTagDevice>())
        {
            sources.Add(($"Read from {device.Name}", () => ReadFromDeviceAsync(device)));
        }

        if (sources.Count == 0)
        {
            if (!_capabilities.HasBluetooth)
            {
                await _dialogs.AlertAsync("Nothing to scan with", _nfc.NoReaderMessage);
                return;
            }

            var connect = await _dialogs.ConfirmAsync(
                "Nothing to scan with",
                _nfc.IsAvailable ? "Connect a puck to read its slots?" : $"{_nfc.NoReaderMessage}\n\nConnect a puck to read its slots instead?",
                "Connect", "Cancel");
            if (connect)
            {
                await _navigator.NavigateAsync(AppRoutes.Devices());
            }

            return;
        }

        if (sources.Count == 1)
        {
            await sources[0].Run();
            return;
        }

        var choice = await _dialogs.ChooseAsync("Scan Tag", sources.Select(s => s.Title).ToList());
        if (choice >= 0)
        {
            await sources[choice].Run();
        }
    }

    public async Task ReadNfcAsync()
    {
        var dump = await _nfc.RunAsync(
            "Hold your tag near the reader.",
            (connection, progress, ct) => AmiiboV3NfcOperations.ReadAnyAsync(connection, progress, ct),
            "Tag read");

        if (dump != null)
        {
            await ShowScannedTagAsync(dump);
        }
    }

    /// <summary>
    /// What happens with any tag read over NFC (Scan Tag, or the main menu's background monitoring): its originality
    /// signature is saved if genuine, then it's opened.
    /// </summary>
    public async Task ShowScannedTagAsync(IAmiiboImage dump, bool automatic = false)
    {
        try
        {
            if (dump is TagDump ntag215)
            {
                await _signatures.AddAsync(ntag215);
            }
        }
        catch
        {
            // Saving the signature is a nice-to-have.
        }

        if (dump is AmiiboV3Dump { HasLockOn: false })
        {
            await _dialogs.AlertAsync(
                "Lock-on not read",
                "The figure's data was read, but its lock-on response didn't become available. Keep the figure on the reader and scan again to include it.");
        }

        await OpenTagAsync(dump, automatic: automatic);
    }

    public async Task ReadFromDeviceAsync(ISlotTagDevice device)
    {
        var slot = await ChooseSlotAsync(device, "Read Slot");
        if (slot == null)
        {
            return;
        }

        var dump = await WithProgressAsync($"Reading {device.Name}", (progress, ct) => device.ReadSlotAsync(slot.Value, progress, ct));
        if (dump != null)
        {
            await OpenTagAsync(dump);
        }
    }

    public async Task LoadTagFromFileAsync()
    {
        var file = await _files.PickFileAsync("Load Tag", TagFileExtensions);
        if (file == null)
        {
            return;
        }

        if (!AmiiboImage.TryParse(file.Data, out var dump) || dump == null)
        {
            await _dialogs.AlertAsync("Invalid file", $"{file.Name} isn't an amiibo dump (expected 532, 540 or 572 bytes for NTAG215 or 2048 bytes for amiibo v3, got {file.Data.Length}).");
            return;
        }

        await OpenTagAsync(dump);
    }

    public async Task ScanQrAsync()
    {
        if (!_qr.IsSupported)
        {
            await _dialogs.AlertAsync("QR scanning unavailable", "QR code scanning isn't supported here.");
            return;
        }

        string? text;
        try
        {
            text = await _qr.ScanAsync();
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("QR scanning failed", ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await OpenTagAsync(AmiiboImage.FromQrText(text));
        }
        catch (TagException ex)
        {
            await _dialogs.AlertAsync("Invalid QR code", ex.Message);
        }
    }

    // ---------------------------------------------------------------- Writing

    public async Task WriteTagAsync(IAmiiboImage image)
    {
        // Only real amiibo data is written: never a blank or foreign image, and never data that fails its signatures.
        var validation = _lookup.Validate(image);
        if (validation.Status is not (AmiiboValidity.Valid or AmiiboValidity.Unverified))
        {
            await _dialogs.AlertAsync(
                validation.Status == AmiiboValidity.Corrupt ? "Damaged amiibo data" : "Not an amiibo",
                $"{string.Join("\n", validation.Problems)}\n\nIt won't be written, as consoles would reject it.");
            return;
        }

        if (image is AmiiboV3Dump v3)
        {
            // v3 amiibo can't be created on blank tags or pucks; only an existing v3 figure's app data can be replaced.
            if (!_nfc.IsAvailable)
            {
                await _dialogs.AlertAsync("Nothing to write with", $"v3 app data can only be written over NFC to a v3 figure of the same character.\n\n{_nfc.NoReaderMessage}");
                return;
            }

            await WriteV3AppDataAsync(v3);
            return;
        }

        if (image is not TagDump dump)
        {
            return;
        }

        var targets = new List<(string Title, Func<Task> Run)>();
        if (_nfc.IsAvailable)
        {
            targets.Add(("NFC tag", () => WriteNfcAsync(dump)));
        }

        foreach (var device in _ble.Connected)
        {
            targets.Add(($"{device.Name} ({TagDeviceDetector.Describe(device.Kind)})", () => WriteToDeviceAsync(device, dump)));
        }

        var canConnect = _capabilities.HasBluetooth;
        if (canConnect)
        {
            targets.Add(("Connect a Bluetooth device...", () => _navigator.NavigateAsync(AppRoutes.Devices())));
        }

        if (targets.Count == 0)
        {
            await _dialogs.AlertAsync("Nothing to write with", _nfc.NoReaderMessage);
            return;
        }

        // Only NFC is ready; behave like the Swift app and go straight to it.
        if (_nfc.IsAvailable && targets.Count == (canConnect ? 2 : 1))
        {
            await targets[0].Run();
            return;
        }

        var choice = await _dialogs.ChooseAsync("Write Tag", targets.Select(t => t.Title).ToList());
        if (choice >= 0)
        {
            await targets[choice].Run();
        }
    }

    /// <summary>
    /// Replaces the app data of the v3 figure on the reader, re-encrypting it for that figure if needed.
    /// </summary>
    public async Task WriteV3AppDataAsync(AmiiboV3Dump dump)
    {
        await _keys.EnsureLoadedAsync();
        var result = await _nfc.RunAsync(
            "Hold the v3 figure near the reader to write its app data.",
            async (connection, progress, ct) => (NfcWriteKind?)await AmiiboV3NfcOperations.WriteAppDataAsync(connection, dump, _keys.Keys, progress, ct),
            "App data written");

        if (result != null)
        {
            await _dialogs.AlertAsync("Figure app data successfully written");
        }
    }

    public async Task WriteNfcAsync(TagDump dump)
    {
        await _keys.EnsureLoadedAsync();
        var result = await _nfc.RunAsync(
            "Hold your tag near the reader to write.",
            async (connection, progress, ct) => (NfcWriteKind?)await AmiiboNfcOperations.WriteTagAsync(connection, dump, _keys.Keys, progress, ct),
            "Tag written");

        if (result == null)
        {
            return;
        }

        await _dialogs.AlertAsync(result switch
        {
            NfcWriteKind.AppData => "Tag app data successfully written",
            NfcWriteKind.Puck => "Puck successfully written",
            _ => "Tag successfully written",
        });
    }

    public async Task WriteToDeviceAsync(ITagDevice device, TagDump dump)
    {
        int? slot = null;
        if (device is ISlotTagDevice slotDevice)
        {
            slot = await ChooseSlotAsync(slotDevice, "Write to Slot");
            if (slot == null)
            {
                return;
            }
        }

        var ok = await WithProgressAsync($"Writing {device.Name}", async (progress, ct) =>
        {
            await device.WriteTagAsync(dump, slot, progress, ct);
            return true;
        });

        if (ok)
        {
            await _dialogs.AlertAsync("Done", $"Written to {device.Name}{(slot != null ? $" slot {slot + 1}" : string.Empty)}.");
        }
    }

    /// <summary>
    /// Lists the device's slots and lets the user pick one. Returns null if cancelled.
    /// </summary>
    public async Task<int?> ChooseSlotAsync(ISlotTagDevice device, string title)
    {
        var slots = await WithProgressAsync($"Reading {device.Name}", (progress, ct) => device.GetSlotsAsync(progress, ct));
        if (slots == null || slots.Count == 0)
        {
            return null;
        }

        var choice = await _dialogs.ChooseAsync(title, slots.Select(s => $"{s.Slot + 1}. {_lookup.GetSlotDisplayName(s.Dump)}").ToList());
        return choice >= 0 ? slots[choice].Slot : null;
    }

    // ---------------------------------------------------------------- Other

    public async Task SaveTagAsync(IAmiiboImage dump)
    {
        await _files.SaveFileAsync($"{FileBaseName(dump)}.bin", dump.Data);
    }

    /// <summary>
    /// Puts a lock-on response into a v3 figure's image (sector 0 pages F0-FF), from a 64 byte .lockon file or a
    /// shorter payload that gets a CRC. Saving or sharing the figure then includes it. Returns null if cancelled or invalid.
    /// </summary>
    public async Task<AmiiboV3Dump?> LoadLockOnAsync(AmiiboV3Dump dump)
    {
        var file = await _files.PickFileAsync("Load Lock-on Response", [".lockon", ".bin"]);
        if (file == null)
        {
            return null;
        }

        if (file.Data.Length is 0 or > LockOnResponse.Length)
        {
            await _dialogs.AlertAsync("Invalid file", $"A lock-on response is up to {LockOnResponse.Length} bytes (got {file.Data.Length}).");
            return null;
        }

        return dump.WithLockOn(file.Data);
    }

    private string FileBaseName(IAmiiboImage dump)
    {
        var name = string.Join("_", _lookup.GetDisplayName(dump).Split(Path.GetInvalidFileNameChars())).Trim();
        return string.IsNullOrEmpty(name) ? "tag" : name;
    }

    /// <summary>
    /// Creates a ready to write tag for a figure. Port of Swift <c>TagDump.FromID</c> + <c>randomizeUID()</c>.
    /// Returns null (after telling the user) if the key file isn't loaded.
    /// </summary>
    public async Task<IAmiiboImage?> CreateFromFigureAsync(AmiiboFigure figure)
    {
        await _keys.EnsureLoadedAsync();
        var keys = _keys.Keys;
        if (keys == null)
        {
            return null;
        }

        if (figure.Id.Id.EndsWith("03", StringComparison.Ordinal))
        {
            // v3 figures: AmiiboZero's NTAG I²C Plus template on a random UID, without a lock-on response.
            return AmiiboV3Dump.Create(figure.Id.Id, keys);
        }

        var dump = TagDump.FromAmiiboId(figure.Id.Id, keys);
        return await GiveNewUidAsync(dump, keys);
    }

    public async Task<IAmiiboImage?> RandomizeUidAsync(IAmiiboImage image)
    {
        await _keys.EnsureLoadedAsync();
        var keys = _keys.Keys;
        if (keys == null)
        {
            await _dialogs.AlertAsync("Key file missing", "Load key_retail.bin in Settings to change a tag's UID.");
            return null;
        }

        try
        {
            return image switch
            {
                AmiiboV3Dump v3 => v3.WithUid(keys),
                TagDump dump => await GiveNewUidAsync(dump, keys),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Couldn't change the UID", ex.Message);
            return null;
        }
    }

    private async Task<TagDump> GiveNewUidAsync(TagDump dump, AmiiboKeySet keys)
    {
        var settings = await _settings.GetAsync();
        if (settings.RandomizeGeneratedUid)
        {
            var (uid, signature) = await _signatures.GetRandomAsync();
            return dump.WithUid(uid, signature, keys);
        }

        return dump.WithUid(NtagUid.Random(), new byte[TagDump.SignatureLength], keys);
    }

    /// <summary>
    /// Runs a device operation with a cancellable progress dialog; errors are shown to the user.
    /// </summary>
    public async Task<T?> WithProgressAsync<T>(string title, Func<IProgress<TransferProgress>, CancellationToken, Task<T>> operation)
    {
        using var cts = new CancellationTokenSource();
        var dialog = _dialogs.ShowProgress("Please Wait", title, cts);
        try
        {
            var progress = new Progress<TransferProgress>(p =>
                dialog.Update(p.Total > 0 ? $"{title} ({p.Current}/{p.Total})" : title, p.Total > 0 ? p.Fraction : null));
            return await operation(progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            await dialog.DisposeAsync();
            await _dialogs.AlertAsync("Something went wrong", ex.Message);
            return default;
        }
        finally
        {
            await dialog.DisposeAsync();
        }
    }
}
