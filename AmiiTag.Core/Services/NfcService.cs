using AmiiTag.Core.Interfaces;
using AmiiTag.Nfc;
using AmiiTag.Tags;

namespace AmiiTag.Core.Services;

/// <summary>
/// Chooses between the registered <see cref="INfcProvider"/> modules and runs NFC reads/writes with UI.
/// </summary>
public sealed class NfcService
{
    private readonly IReadOnlyList<INfcProvider> _providers;
    private readonly SettingsStore _settings;
    private readonly IDialogService _dialogs;

    public NfcService(IEnumerable<INfcProvider> providers, SettingsStore settings, IDialogService dialogs)
    {
        _providers = providers.ToList();
        _settings = settings;
        _dialogs = dialogs;
    }

    /// <summary>
    /// Every registered provider, supported or not.
    /// </summary>
    public IReadOnlyList<INfcProvider> AllProviders => _providers;

    public IReadOnlyList<INfcProvider> SupportedProviders => _providers.Where(p => SafeIsSupported(p)).ToList();

    public bool IsAvailable => SupportedProviders.Count > 0;

    public async Task<INfcProvider?> GetSelectedProviderAsync()
    {
        var settings = await _settings.GetAsync();
        ApplySerialSettings(settings);
        var supported = SupportedProviders;
        return supported.FirstOrDefault(p => p.Id == settings.NfcProviderId) ?? supported.FirstOrDefault();
    }

    private void ApplySerialSettings(AppSettings settings)
    {
        foreach (var serial in _providers.OfType<INfcBaudRateSetting>())
        {
            var rate = serial.SupportedBaudRates.Contains(settings.Mfrc522BaudRate) ? settings.Mfrc522BaudRate : serial.DefaultBaudRate;
            if (serial.BaudRate != rate)
            {
                serial.BaudRate = rate;
            }
        }
    }

    /// <summary>
    /// Changes the serial reader speed. The reader is released (the chip goes back to 9600) so the next scan connects
    /// at the new rate.
    /// </summary>
    public async Task SetSerialBaudRateAsync(int baudRate)
    {
        await CancelMonitorAsync();
        try
        {
            var settings = await _settings.GetAsync();
            settings.Mfrc522BaudRate = baudRate;
            await _settings.SaveAsync();
            ApplySerialSettings(settings);

            foreach (var releasable in _providers.OfType<INfcBaudRateSetting>().OfType<INfcReleasable>())
            {
                try
                {
                    await releasable.ReleaseAsync();
                }
                catch
                {
                }
            }
        }
        finally
        {
            ResumeMonitor();
        }
    }

    public async Task SelectAsync(string providerId, string? readerId)
    {
        await CancelMonitorAsync();
        try
        {
            var settings = await _settings.GetAsync();
            settings.NfcProviderId = providerId;
            settings.NfcReaderId = readerId;
            await _settings.SaveAsync();

            // Let go of ports/sessions held by the previous reader (an MFRC522 keeps its COM port open between scans).
            foreach (var releasable in _providers.OfType<INfcReleasable>())
            {
                try
                {
                    await releasable.ReleaseAsync();
                }
                catch
                {
                }
            }
        }
        finally
        {
            ResumeMonitor();
        }
    }

    // ------------------------------------------------------------------ Background monitoring

    private Func<IAmiiboImage, Task>? _onTagScanned;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private int _busy;

    /// <summary>
    /// The last tag seen on the reader (shown by the monitor, used by a scan/write, or placed while away from the main
    /// menu). While it stays on the reader the main menu won't open it. It's forgotten as soon as it's gone: away from
    /// the main menu a presence-only check keeps watching (it never reads or opens anything), so only a tag placed while
    /// the main menu is in front opens automatically.
    /// </summary>
    private byte[]? _lastUid;

    private void Remember(byte[] uid) => _lastUid = uid;

    /// <summary>
    /// Set once the main menu has started monitoring: from then on the loop keeps tracking the reader away from the
    /// main menu too (presence only), so tags placed or removed there are known when it comes back.
    /// </summary>
    private bool _tracking;

    public bool IsMonitoring => _monitorTask is { IsCompleted: false };

    /// <summary>
    /// Watches the selected reader in the background (only readers without a system prompt, so not iOS) and calls
    /// <paramref name="onTagScanned"/> with each amiibo placed on it. A tag is reported once until it's taken off.
    /// Pauses automatically while <see cref="RunAsync{T}"/> uses the reader. Never shows prompts or errors.
    /// </summary>
    public void StartMonitoring(Func<IAmiiboImage, Task> onTagScanned)
    {
        _onTagScanned = onTagScanned;
        _tracking = true;
        ResumeMonitor();
    }

    public async Task StopMonitoringAsync()
    {
        _onTagScanned = null;
        await CancelMonitorAsync();

        // Keep watching for the last tag to be taken off.
        ResumeMonitor();
    }

    /// <summary>
    /// Runs the loop: showing tags while the main menu is in front, tracking which tag is on the reader otherwise.
    /// </summary>
    private void ResumeMonitor()
    {
        if ((_onTagScanned != null || _tracking) && _busy == 0 && !IsMonitoring)
        {
            _monitorCts = new CancellationTokenSource();
            _monitorTask = MonitorLoopAsync(_monitorCts.Token);
        }
    }

    private async Task CancelMonitorAsync()
    {
        var cts = _monitorCts;
        var task = _monitorTask;
        _monitorCts = null;
        _monitorTask = null;
        if (cts == null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            if (task != null)
            {
                await task;
            }
        }
        catch
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        while (!cancellationToken.IsCancellationRequested)
        {
            // Away from the main menu, this loop only tracks which tag is on the reader (it never reads or opens one).
            var presenceOnly = _onTagScanned == null;

            var provider = await GetSelectedProviderAsync();
            if (provider == null || provider.ShowsSystemPrompt)
            {
                return;
            }

            var settings = await _settings.GetAsync();
            var readerId = settings.NfcProviderId == provider.Id ? settings.NfcReaderId : null;

            // A known tag is still on the reader: where the reader can say so without a connection (PC/SC status), ask
            // that rather than reconnecting every half second, which makes some readers reset the tag and beep each time.
            if (_lastUid != null && provider is INfcPresenceProvider presence)
            {
                bool? present;
                try
                {
                    present = await presence.IsTagPresentAsync(readerId, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (present == true)
                {
                    if (!await DelayAsync(TimeSpan.FromMilliseconds(400), cancellationToken))
                    {
                        return;
                    }

                    continue;
                }

                if (present == false)
                {
                    // Taken off.
                    _lastUid = null;
                    continue;
                }

                // Unknown: fall back to connecting.
            }

            INfcTagConnection? connection = null;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_lastUid != null)
                {
                    // Checking whether the last tag is still there.
                    attempt.CancelAfter(TimeSpan.FromMilliseconds(700));
                }

                connection = await provider.ConnectAsync(new NfcConnectOptions { ReaderId = readerId, AllowUserPrompts = false }, attempt.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Nothing on the reader: the last tag was taken off.
                _lastUid = null;
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NFC monitor: connect failed ({provider.DisplayName}): {ex.Message}");

                // No reader, an invalid one, or it was unplugged: try again later, quietly.
                if (!await DelayAsync(TimeSpan.FromSeconds(3), cancellationToken))
                {
                    return;
                }

                continue;
            }

            IAmiiboImage? image = null;
            try
            {
                if (presenceOnly && !(_lastUid != null && connection.Uid.Length > 0 && connection.Uid.AsSpan().SequenceEqual(_lastUid)))
                {
                    // A tag was placed while away from the main menu: it becomes the last tag, so it isn't opened when
                    // the main menu comes back. Keep watching it until it's taken off.
                    _lastUid = connection.Uid.Length > 0 ? connection.Uid : null;
                    await connection.CloseAsync();
                    connection = null;
                    if (!await DelayAsync(TimeSpan.FromMilliseconds(500), cancellationToken))
                    {
                        return;
                    }

                    continue;
                }

                if (_lastUid != null && connection.Uid.Length > 0 && connection.Uid.AsSpan().SequenceEqual(_lastUid))
                {
                    // Same tag still on the reader.
                    await connection.CloseAsync();
                    connection = null;
                    if (!await DelayAsync(TimeSpan.FromMilliseconds(500), cancellationToken))
                    {
                        return;
                    }

                    continue;
                }

                image = await AmiiboV3NfcOperations.ReadAnyAsync(connection, null, cancellationToken);
                Remember(connection.Uid.Length > 0 ? connection.Uid : image.Uid);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NFC monitor: read failed: {ex}");

                // Moved away mid-read, or not something we can read: wait for it to be taken off.
                Remember(connection?.Uid is { Length: > 0 } uid ? uid : Array.Empty<byte>());
            }
            finally
            {
                if (connection != null)
                {
                    try
                    {
                        await connection.CloseAsync();
                    }
                    catch
                    {
                    }
                }
            }

            if (image != null && _onTagScanned is { } handler)
            {
                // Not awaited: showing the tag navigates away, which stops this loop.
                _ = SafeInvokeAsync(handler, image);
            }
        }
    }

    private static async Task SafeInvokeAsync(Func<IAmiiboImage, Task> handler, IAmiiboImage image)
    {
        try
        {
            await handler(image);
        }
        catch
        {
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Connects to a tag with the selected provider, runs <paramref name="operation"/>, and closes the session.
    /// Shows a "hold your tag" dialog unless the OS shows its own.
    /// </summary>
    public async Task<T?> RunAsync<T>(string prompt, Func<INfcTagConnection, IProgress<double>, CancellationToken, Task<T>> operation, string? successMessage = null)
    {
        // The background monitor shares the reader, so it pauses for the duration.
        _busy++;
        await CancelMonitorAsync();
        try
        {
            return await RunCoreAsync(prompt, operation, successMessage);
        }
        finally
        {
            _busy--;
            ResumeMonitor();
        }
    }

    private async Task<T?> RunCoreAsync<T>(string prompt, Func<INfcTagConnection, IProgress<double>, CancellationToken, Task<T>> operation, string? successMessage)
    {
        var provider = await GetSelectedProviderAsync();
        if (provider == null)
        {
            await _dialogs.AlertAsync("NFC unavailable", NoReaderMessage);
            return default;
        }

        var settings = await _settings.GetAsync();
        var readerId = settings.NfcProviderId == provider.Id ? settings.NfcReaderId : null;

        using var cts = new CancellationTokenSource();
        IProgressDialog? dialog = provider.ShowsSystemPrompt ? null : _dialogs.ShowProgress("Scan Tag", prompt, cts);
        INfcTagConnection? connection = null;
        try
        {
            connection = await provider.ConnectAsync(new NfcConnectOptions { Prompt = prompt, ReaderId = readerId }, cts.Token);
            if (connection.Uid.Length > 0)
            {
                // The monitor shouldn't reopen a tag the user just scanned or wrote.
                Remember(connection.Uid);
            }

            dialog?.Update("Hold still...");
            var progress = new Progress<double>(fraction => dialog?.Update("Hold still...", fraction));
            var result = await operation(connection, progress, cts.Token);
            if (successMessage != null)
            {
                connection.SetStatus(successMessage);
            }

            await connection.CloseAsync();
            connection = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NFC ({provider.DisplayName}): {ex}");
            if (connection != null)
            {
                await connection.CloseAsync(ex.Message);
                connection = null;
            }

            if (dialog != null)
            {
                await dialog.DisposeAsync();
                dialog = null;
            }

            await _dialogs.AlertAsync("NFC error", ex.Message);
            return default;
        }
        finally
        {
            if (connection != null)
            {
                await connection.DisposeAsync();
            }

            if (dialog != null)
            {
                await dialog.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Explains why no NFC provider can be used here.
    /// </summary>
    public string NoReaderMessage => _providers.Count == 0
        ? "This version of AmiiTag has no NFC support."
        : $"No NFC reader is available ({string.Join(", ", _providers.Select(p => p.DisplayName))}). " +
          "In a browser, NFC needs an MFRC522 or PN532 reader on a USB serial adapter (Web Serial), which works in Chrome or Edge on desktop. " +
          "On Windows, connect a USB NFC reader or an MFRC522 or PN532 serial reader.";

    private static bool SafeIsSupported(INfcProvider provider)
    {
        try
        {
            return provider.IsSupported;
        }
        catch
        {
            return false;
        }
    }
}
