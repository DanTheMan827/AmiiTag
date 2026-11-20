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
    private readonly N2EliteService _n2;
    private readonly LockOnLibrary _lockOns;

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
        DeviceCapabilities capabilities,
        N2EliteService n2,
        LockOnLibrary lockOns)
    {
        _lockOns = lockOns;
        _capabilities = capabilities;
        _n2 = n2;
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
                await OpenBluetoothDevicesAsync();
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
    public async Task ShowScannedTagAsync(IAmiiboImage dump, bool automatic = false, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;
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

        if (cancellationToken.IsCancellationRequested) return;
        if (dump is AmiiboV3Dump { HasLockOn: false })
        {
            await _dialogs.AlertAsync(
                "Lock-on not read",
                "The figure's data was read, but its lock-on response didn't become available. Keep the figure on the reader and scan again to include it.");
        }

        // The Home page may have been left while saving the signature or
        // showing a warning. A stale polling result must not open a new route.
        if (!cancellationToken.IsCancellationRequested)
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
            targets.Add(("N2 Elite bank...", () => _n2.WriteToChosenBankAsync(dump)));
        }

        // Devices already connected stay connected afterwards.
        foreach (var device in _ble.Connected)
        {
            targets.Add(($"{device.Name} ({TagDeviceDetector.Describe(device.Kind)})", () => WriteToDeviceAsync(device, dump)));
        }

        // Devices the background scan found: connect, write (choosing a slot or file name), disconnect.
        var nearby = NearbyTagDevices();
        foreach (var info in nearby)
        {
            var kind = TagDeviceDetector.GuessKind(info);
            var description = kind == TagDeviceKind.Unknown ? "Bluetooth" : TagDeviceDetector.Describe(kind);
            targets.Add(($"{info.DisplayName} ({description})", () => ConnectWriteDisconnectAsync(info, dump)));
        }

        var canConnect = _capabilities.HasBluetooth;
        if (canConnect)
        {
            // Browsers can't scan: their device picker is the list. The apps fall back to the device screen.
            if (UsesBrowserDevicePicker)
            {
                targets.Add(("Bluetooth device...", () => PickWriteDisconnectAsync(dump)));
            }
            else
            {
                targets.Add((nearby.Count > 0 ? "Other Bluetooth device..." : "Connect a Bluetooth device...", () => OpenBluetoothDevicesAsync()));
            }
        }

        if (targets.Count == 0)
        {
            await _dialogs.AlertAsync("Nothing to write with", _nfc.NoReaderMessage);
            return;
        }

        // Only NFC is ready (no Bluetooth device connected or nearby); behave like the Swift app and go straight to it.
        if (_nfc.IsAvailable && _ble.Connected.Count == 0 && nearby.Count == 0)
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
        if (await WriteToDeviceCoreAsync(device, dump) is { } done)
        {
            await _dialogs.AlertAsync("Done", done);
        }
    }

    /// <summary>
    /// Writes to a connected device, asking for a slot or file name where it has them. Returns what to tell the user
    /// when it worked, or null if it was cancelled or failed (failures are shown).
    /// </summary>
    private async Task<string?> WriteToDeviceCoreAsync(ITagDevice device, TagDump dump)
    {
        if (device is IFileTagDevice files)
        {
            return await UploadToFileDeviceAsync(files, dump, null) ? $"Uploaded to {files.Name}." : null;
        }

        if (device is ITagListDevice list)
        {
            // Added as a new tag, named after the figure.
            var name = _lookup.GetDisplayName(dump);
            var added = await WithProgressAsync($"Uploading to {list.Name}", async (progress, ct) =>
            {
                await list.UploadTagsAsync([(name, dump)], progress, ct);
                return true;
            });
            return added ? $"Added {name} to {list.Name}." : null;
        }

        int? slot = null;
        if (device is ISlotTagDevice slotDevice)
        {
            slot = await ChooseSlotAsync(slotDevice, "Write to Slot");
            if (slot == null)
            {
                return null;
            }
        }

        var ok = await WithProgressAsync($"Writing {device.Name}", async (progress, ct) =>
        {
            await device.WriteTagAsync(dump, slot, progress, ct);
            return true;
        });

        return ok ? $"Written to {device.Name}{(slot != null ? $" slot {slot + 1}" : string.Empty)}." : null;
    }

    /// <summary>
    /// Connects to a nearby device, writes to it and disconnects again (the "Done" message comes after disconnecting).
    /// </summary>
    public async Task ConnectWriteDisconnectAsync(BleDeviceInfo info, TagDump dump)
    {
        if (await ConnectDeviceAsync(info) is not { } device)
        {
            return;
        }

        string? done;
        try
        {
            done = await WriteToDeviceCoreAsync(device, dump);
        }
        finally
        {
            await _ble.DisconnectAsync(device);
        }

        if (done != null)
        {
            await _dialogs.AlertAsync("Done", done);
        }
    }

    /// <summary>
    /// Browsers: the device picker, then connect, write and disconnect.
    /// </summary>
    private async Task PickWriteDisconnectAsync(TagDump dump)
    {
        BleDeviceInfo? info;
        try
        {
            info = await _ble.RequestDeviceAsync();
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Bluetooth", ex.Message);
            return;
        }

        if (info != null)
        {
            await ConnectWriteDisconnectAsync(info, dump);
        }
    }

    /// <summary>
    /// Devices the scan has found that look like tag devices (by name or service) and aren't connected.
    /// </summary>
    public IReadOnlyList<BleDeviceInfo> NearbyTagDevices() =>
        _ble.Discovered
            .Where(d => _ble.GetConnected(d.Id) == null &&
                        (TagDeviceDetector.GuessKind(d) != TagDeviceKind.Unknown || _ble.Filter.Matches(d)))
            .ToList();

    /// <summary>
    /// Scans in the background (apps only) once Bluetooth has been used, so the permission prompt never comes out of the
    /// blue. Returns true if this started the scan (the caller stops it).
    /// </summary>
    public async Task<bool> StartBackgroundBluetoothScanAsync()
    {
        if (!_capabilities.HasBluetooth || !_ble.Adapter.CanScan || _ble.IsScanning)
        {
            return false;
        }

        var settings = await _settings.GetAsync();
        if (!settings.BluetoothUsed)
        {
            return false;
        }

        return await _ble.StartScanningAsync() == null && _ble.IsScanning;
    }

    /// <summary>
    /// Records that Bluetooth has been used (permission granted), enabling background scans.
    /// </summary>
    public async Task RememberBluetoothUsedAsync()
    {
        var settings = await _settings.GetAsync();
        if (!settings.BluetoothUsed)
        {
            settings.BluetoothUsed = true;
            await _settings.SaveAsync();
        }
    }

    /// <summary>
    /// Asks for a file name (the tag's name by default) and uploads the tag to a file based device such as a Pixl.js.
    /// Returns false if cancelled or it failed.
    /// </summary>
    public async Task<bool> UploadToFileDeviceAsync(IFileTagDevice device, TagDump dump, string? folder)
    {
        var target = folder ?? device.DefaultFolder;
        var name = await _dialogs.PromptAsync("File Name", $"Save to {target} on {device.Name} as:", FileBaseName(dump), 43);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Trim();
        if (!name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
        {
            name += ".bin";
        }

        if (PixlPath.ValidateName(name, target) is { } problem)
        {
            await _dialogs.AlertAsync("Invalid name", problem);
            return false;
        }

        var ok = await WithProgressAsync($"Uploading to {device.Name}", async (progress, ct) =>
        {
            await device.UploadTagAsync(name, dump, target, progress, ct);
            return true;
        });

        return ok;
    }

    // ---------------------------------------------------------------- Bluetooth devices

    /// <summary>
    /// True in browsers: they can't scan, but show their own device picker, so there's no device list screen.
    /// </summary>
    public bool UsesBrowserDevicePicker => _ble.Adapter.CanRequestDevice && !_ble.Adapter.CanScan;

    /// <summary>
    /// Where Bluetooth devices are managed from: the device list (apps), or straight to the browser's picker and then
    /// the chosen device's screen (web). Must be called from a user gesture in the browser.
    /// </summary>
    public async Task OpenBluetoothDevicesAsync()
    {
        if (!UsesBrowserDevicePicker)
        {
            await _navigator.NavigateAsync(AppRoutes.Devices());
            return;
        }

        if (!_ble.Adapter.IsAvailable)
        {
            if (_capabilities.Messages.SuggestsBeacio)
            {
                if (await _dialogs.ConfirmAsync("Bluetooth unavailable", _capabilities.Messages.NoBluetooth, "Get Beacio", "OK"))
                {
                    await _navigator.OpenExternalAsync(PlatformMessages.BeacioUrl);
                }
            }
            else
            {
                await _dialogs.AlertAsync("Bluetooth unavailable", _capabilities.Messages.NoBluetooth);
            }

            return;
        }

        BleDeviceInfo? device;
        try
        {
            device = await _ble.RequestDeviceAsync();
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Bluetooth", ex.Message);
            return;
        }

        if (device != null && await ConnectDeviceAsync(device) is { } connected)
        {
            await _navigator.NavigateAsync(AppRoutes.Device(connected.Info.Id, connected.Name));
        }
    }

    /// <summary>
    /// Connects to a device (identifying what it is) behind a progress dialog. Returns null if cancelled or it failed.
    /// </summary>
    public async Task<ITagDevice?> ConnectDeviceAsync(BleDeviceInfo device, TagDeviceKind? kind = null)
    {
        // Pucks with amiitool support get our keys while connecting, so have them loaded first.
        await _keys.EnsureLoadedAsync();
        using var cts = new CancellationTokenSource();
        var dialog = _dialogs.ShowProgress("Please Wait", $"Connecting to {device.DisplayName}...", cts);
        try
        {
            var status = new Progress<string>(message => dialog.Update(message));
            var connected = await _ble.ConnectAsync(device, kind, status, cts.Token);
            await RememberBluetoothUsedAsync();
            return connected;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            await dialog.DisposeAsync();
            await _dialogs.AlertAsync("Couldn't connect", ex.Message);
            return null;
        }
        finally
        {
            await dialog.DisposeAsync();
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
    /// Puts a lock-on response from a file (a .lockon, or a v3 .bin that has one) into a v3 figure's image (sector 0
    /// pages F0-FF), offering to add it to the lock-on library. Saving or sharing the figure then includes it.
    /// Returns null if cancelled or invalid. (The library itself is offered by the Load Lock-on page.)
    /// </summary>
    public async Task<AmiiboV3Dump?> LoadLockOnFromFileAsync(AmiiboV3Dump dump)
    {
        var file = await _files.PickFileAsync("Load Lock-on Response", LockOnFileExtensions);
        return file == null ? null : await UseLockOnFileAsync(dump, file);
    }

    /// <summary>
    /// Puts the lock-on from a file (picked or dropped) into a v3 image, telling the user why if the file doesn't hold
    /// a valid one, and offering to add it to the library. Returns null if it can't be used.
    /// </summary>
    public async Task<AmiiboV3Dump?> UseLockOnFileAsync(AmiiboV3Dump dump, PickedFile file)
    {
        await _lockOns.EnsureLoadedAsync();
        if (ReadLockOnFile(file, out var problem) is not { } found)
        {
            await _dialogs.AlertAsync("No valid lock-on", problem);
            return null;
        }

        var updated = dump.WithLockOn(found.Response);
        if (_lockOns.Find(updated.LockOn) == null &&
            await _dialogs.ConfirmAsync("Add to Lock-on Library?", "Keep this lock-on in your library to use it again later.", "Add", "Not Now"))
        {
            await SaveLockOnToLibraryAsync(updated, found.Name);
        }

        return updated;
    }

    /// <summary>
    /// Adds a v3 image's lock-on response to the library under a name the user gives it. Returns the entry (an
    /// existing one if it's already there), or null if cancelled.
    /// </summary>
    public async Task<LockOnEntry?> SaveLockOnToLibraryAsync(AmiiboV3Dump dump, string? suggestedName = null)
    {
        if (dump.LockOn is not { } response)
        {
            return null;
        }

        var check = LockOnResponse.Check(response);
        if (!LockOnResponse.IsUsable(check))
        {
            await _dialogs.AlertAsync("Not a lock-on response", $"{LockOnResponse.Describe(check)}. Read the figure again.");
            return null;
        }

        if (check == LockOnCheck.CrcMismatch &&
            !await _dialogs.ConfirmAsync("CRC doesn't match", "This lock-on's CRC doesn't match its data, so it may be damaged. Save it anyway?", "Save", "Cancel"))
        {
            return null;
        }

        await _lockOns.EnsureLoadedAsync();
        if (_lockOns.Find(response) is { } existing)
        {
            await _dialogs.AlertAsync("Already in your library", $"This lock-on is saved as \u201C{existing.Name}\u201D.");
            return existing;
        }

        var name = await _dialogs.PromptAsync("Name This Lock-on", "Shown in your lock-on library.", suggestedName ?? _lookup.GetDisplayName(dump), 60);
        if (name == null)
        {
            return null;
        }

        var amiiboId = dump.HasAmiiboId ? dump.AmiiboIdHex : null;
        return (await _lockOns.AddAsync(name, response, amiiboId, dump.UidHex)).Entry;
    }

    /// <summary>
    /// Files that can hold a lock-on: .lockon responses, library entries saved as .json, and v3 .bin images.
    /// </summary>
    public static readonly IReadOnlyList<string> LockOnFileExtensions = [".lockon", ".json", ".bin"];

    /// <summary>
    /// Adds lock-on files to the library: .lockon responses, library entries (.json), or v3 .bin images that include
    /// one. One file is named by
    /// the user; several keep the file's name (or a library entry's own), which can be changed later.
    /// </summary>
    public async Task ImportLockOnsAsync()
    {
        var files = await _files.PickFilesAsync("Import Lock-ons", LockOnFileExtensions);
        if (files.Count > 0)
        {
            await AddLockOnsAsync(files);
        }
    }

    /// <summary>
    /// Adds lock-on files (picked or dropped) to the library. Each is named by the user when <paramref name="askNames"/>
    /// (by default, when there's only one); otherwise it keeps the file's name (or a library entry's own).
    /// </summary>
    public async Task AddLockOnsAsync(IReadOnlyList<PickedFile> files, bool? askNames = null)
    {
        var ask = askNames ?? files.Count == 1;
        await _lockOns.EnsureLoadedAsync();
        var added = 0;
        var duplicates = new List<string>();
        var invalid = new List<string>();
        foreach (var file in files)
        {
            if (ReadLockOnFile(file, out var problem) is not { } found)
            {
                invalid.Add(problem);
                continue;
            }

            if (_lockOns.Find(LockOnResponse.Normalize(found.Response)) is { } existing)
            {
                duplicates.Add($"{file.Name} (saved as \u201C{existing.Name}\u201D)");
                continue;
            }

            var name = found.Name;
            if (ask)
            {
                var given = await _dialogs.PromptAsync("Name This Lock-on", $"{file.Name}: shown in your lock-on library.", found.Name, 60);
                if (given == null)
                {
                    continue;
                }

                name = given;
            }

            await _lockOns.AddAsync(name, found.Response, found.AmiiboId, found.Uid);
            added++;
        }

        if (duplicates.Count > 0 || invalid.Count > 0)
        {
            var lines = new List<string>();
            if (added > 0)
            {
                lines.Add($"Added {added}.");
            }

            if (duplicates.Count > 0)
            {
                lines.Add($"Already in your library: {string.Join(", ", duplicates)}.");
            }

            lines.AddRange(invalid);
            await _dialogs.AlertAsync("Import Lock-ons", string.Join("\n\n", lines));
        }
    }

    public async Task RenameLockOnAsync(LockOnEntry entry)
    {
        var name = await _dialogs.PromptAsync("Rename Lock-on", null, entry.Name, 60);
        if (name != null)
        {
            await _lockOns.RenameAsync(entry, name);
        }
    }

    public Task ExportLockOnAsync(LockOnEntry entry)
    {
        var name = string.Join("_", entry.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        return _files.SaveFileAsync($"{(name.Length == 0 ? "lock-on" : name)}.lockon", entry.Response);
    }

    public async Task DeleteLockOnAsync(LockOnEntry entry)
    {
        if (await _dialogs.ConfirmAsync($"Delete \u201C{entry.Name}\u201D?", "It's removed from your lock-on library. Tags it was loaded into keep their copy.", "Delete", "Cancel"))
        {
            await _lockOns.DeleteAsync(entry);
        }
    }

    /// <summary>
    /// A lock-on from a file: a 1-64 byte response, or a v3 amiibo image that includes one. Null if it's neither, or it
    /// doesn't pass the checks that can be made without the figure's keys, with the reason in <paramref name="problem"/>.
    /// </summary>
    private (byte[] Response, string Name, string? AmiiboId, string? Uid)? ReadLockOnFile(PickedFile file, out string problem)
    {
        problem = "";

        // A library entry saved as JSON keeps its name, figure and source UID.
        if (LockOnLibrary.TryReadEntryJson(file.Data) is { } entry)
        {
            var entryCheck = LockOnResponse.Check(LockOnResponse.Normalize(entry.Response));
            if (!LockOnResponse.IsUsable(entryCheck))
            {
                problem = $"{file.Name} is a lock-on library entry, but its lock-on isn't valid: {LockOnResponse.Describe(entryCheck)}.";
                return null;
            }

            var entryName = string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileNameWithoutExtension(file.Name) : entry.Name;
            return (entry.Response, entryName, entry.AmiiboId, entry.SourceUid);
        }

        if (Path.GetExtension(file.Name).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"{file.Name} isn't a lock-on library entry (a JSON object with a base 64 Response of up to {LockOnResponse.Length} bytes).";
            return null;
        }
        if (file.Data.Length == AmiiboV3Dump.ImageLength && AmiiboImage.TryParse(file.Data, out var image) && image is AmiiboV3Dump v3)
        {
            if (v3.LockOn is not { } response)
            {
                problem = $"{file.Name} is a v3 amiibo without a lock-on.";
                return null;
            }

            var check = LockOnResponse.Check(response);
            if (!LockOnResponse.IsUsable(check))
            {
                problem = $"{file.Name} is a v3 amiibo, but its lock-on isn't valid: {LockOnResponse.Describe(check)}.";
                return null;
            }

            // Named after the file (the user named it), not the figure: several dumps of one character stay apart.
            return (response, Path.GetFileNameWithoutExtension(file.Name), v3.HasAmiiboId ? v3.AmiiboIdHex : null, v3.UidHex);
        }

        if (AmiiboImage.TryParse(file.Data, out var other) && other is TagDump)
        {
            problem = $"{file.Name} is an NTAG215 amiibo. Only v3 figures have a lock-on.";
            return null;
        }

        if (file.Data.Length is > 0 and <= LockOnResponse.BodyLength or LockOnResponse.Length)
        {
            var check = LockOnResponse.Check(LockOnResponse.Normalize(file.Data));
            if (!LockOnResponse.IsUsable(check))
            {
                problem = $"{file.Name} isn't a valid lock-on: {LockOnResponse.Describe(check)}.";
                return null;
            }

            return (file.Data, Path.GetFileNameWithoutExtension(file.Name), null, null);
        }

        problem = $"{file.Name} isn't a lock-on ({LockOnResponse.Length} bytes) or a v3 amiibo that includes one ({file.Data.Length} bytes).";
        return null;
    }

    /// <summary>
    /// Imports key files (picked or dropped): key_retail.bin, or unfixed-info.bin and locked-secret.bin, recognised by
    /// size and SHA-256, and says what was loaded.
    /// </summary>
    public async Task ImportKeysAsync(IReadOnlyList<PickedFile> files)
    {
        try
        {
            var result = await _keys.ImportAsync(files);
            var message = $"Loaded: {string.Join(", ", result.Used)}.";
            if (result.Ignored.Count > 0)
            {
                message += $"\n\nIgnored (not key files): {string.Join(", ", result.Ignored)}.";
            }

            if (!result.HasBothKeys)
            {
                message += result.HasUnfixedInfo
                    ? "\n\nStill needed to encrypt: locked-secret.bin."
                    : "\n\nStill needed: unfixed-info.bin.";
            }

            await _dialogs.AlertAsync(result.HasBothKeys ? "Keys loaded" : "Key loaded", message);
        }
        catch (TagException ex)
        {
            await _dialogs.AlertAsync("No key files", ex.Message);
        }
    }

    /// <summary>
    /// Files dropped on the main menu, recognised by their contents: key files are imported, lock-ons named and added
    /// to the library, UID/signature pairs checked and saved, and an amiibo opened.
    /// </summary>
    public async Task HandleDroppedFilesAsync(IReadOnlyList<PickedFile> files)
    {
        var keys = new List<PickedFile>();
        var lockOns = new List<PickedFile>();
        var signatures = new List<PickedFile>();
        var amiibo = new List<(PickedFile File, IAmiiboImage Image)>();
        var unknown = new List<string>();
        foreach (var file in files)
        {
            var isLockOnFile = Path.GetExtension(file.Name).Equals(".lockon", StringComparison.OrdinalIgnoreCase) ||
                LockOnLibrary.TryReadEntryJson(file.Data) != null;
            if (AmiiboKeyFiles.Identify(file.Data) != AmiiboKeyFileKind.Unknown)
            {
                keys.Add(file);
            }
            else if (AmiiboImage.TryParse(file.Data, out var image) && image != null)
            {
                amiibo.Add((file, image));
            }
            else if (isLockOnFile || file.Data.Length == LockOnResponse.Length)
            {
                lockOns.Add(file);
            }
            else if (file.Data.Length == SignatureStore.RecordLength)
            {
                signatures.Add(file);
            }
            else
            {
                unknown.Add(file.Name);
            }
        }

        if (keys.Count > 0)
        {
            await ImportKeysAsync(keys);
        }

        if (signatures.Count > 0)
        {
            var lines = new List<string>();
            foreach (var file in signatures)
            {
                lines.Add((await _signatures.AddRecordAsync(file.Data)) switch
                {
                    SignatureImport.Added => $"{file.Name}: saved.",
                    SignatureImport.AlreadySaved => $"{file.Name}: already saved.",
                    SignatureImport.InvalidUid => $"{file.Name}: not saved, the UID's check bytes don't match it.",
                    SignatureImport.InvalidSignature => $"{file.Name}: not saved, the signature isn't valid for this UID.",
                    _ => $"{file.Name}: not saved, a UID/signature pair is {SignatureStore.RecordLength} bytes.",
                });
            }

            await _dialogs.AlertAsync("UID/Signature Pairs", string.Join("\n", lines));
        }

        if (lockOns.Count > 0)
        {
            await AddLockOnsAsync(lockOns, askNames: true);
        }

        if (unknown.Count > 0)
        {
            await _dialogs.AlertAsync("Not recognised",
                $"{string.Join(", ", unknown)}: not an amiibo, a key file, a lock-on (.lockon, a library .json, or {LockOnResponse.Length} bytes) or a UID/signature pair ({SignatureStore.RecordLength} bytes).");
        }

        if (amiibo.Count > 1)
        {
            await _dialogs.AlertAsync("One amiibo at a time", $"Opening {amiibo[0].File.Name}; drop the others one by one.");
        }

        if (amiibo.Count > 0)
        {
            await OpenTagAsync(amiibo[0].Image);
        }
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
            // v3 figures: AmiiboZero's NTAG I²C Plus template on a random UID, with the first lock-on in the library
            // for this figure if there is one.
            var v3 = AmiiboV3Dump.Create(figure.Id.Id, keys);
            await _lockOns.EnsureLoadedAsync();
            return _lockOns.FirstFor(figure.Id.Id) is { } lockOn ? v3.WithLockOn(lockOn.Response) : v3;
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
    /// How long a device operation may go without finishing or reporting progress before it's given up on. The drivers
    /// time out their own commands well before this; it's a backstop so a blocking dialog can't stay up for good.
    /// </summary>
    public static readonly TimeSpan OperationStallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs a device operation behind a progress dialog that blocks the app until it finishes; errors are shown to the
    /// user. Once a command has started on a device it can't be cancelled, so there's no Cancel button unless
    /// <paramref name="cancellable"/>; the dialog closes when the operation succeeds, fails or times out (its own
    /// timeouts, or <see cref="OperationStallTimeout"/> without progress).
    /// </summary>
    public async Task<T?> WithProgressAsync<T>(string title, Func<IProgress<TransferProgress>, CancellationToken, Task<T>> operation, bool cancellable = false)
    {
        using var cancel = new CancellationTokenSource();
        using var stalled = new CancellationTokenSource(OperationStallTimeout);
        using var token = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, stalled.Token);
        var dialog = _dialogs.ShowProgress("Please Wait", title, cancellable ? cancel : null);
        try
        {
            var progress = new Progress<TransferProgress>(p =>
            {
                // Progress means it's still going: the stall clock starts over (a report can arrive after the end).
                try
                {
                    stalled.CancelAfter(OperationStallTimeout);
                }
                catch (ObjectDisposedException)
                {
                }

                dialog.Update(p.Total > 0 ? $"{title} ({p.Current}/{p.Total})" : title, p.Total > 0 ? p.Fraction : null);
            });

            // A driver that doesn't watch the token still can't keep the dialog up: past the stall timeout the
            // operation is left to finish (or fail) on its own, unobserved.
            var running = operation(progress, token.Token);
            var gaveUp = new TaskCompletionSource();
            using (stalled.Token.Register(() => gaveUp.TrySetResult()))
            {
                if (await Task.WhenAny(running, gaveUp.Task) != running)
                {
                    _ = running.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    throw new OperationCanceledException(stalled.Token);
                }
            }

            return await running;
        }
        catch (OperationCanceledException) when (stalled.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            await dialog.DisposeAsync();
            await _dialogs.AlertAsync("The device stopped responding", $"{title} didn't finish. Check the device is nearby and switched on, then try again.");
            return default;
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
