using AmiiTag.Bluetooth;
using AmiiTag.Bluetooth.Devices;
using AmiiTag.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;

namespace AmiiTag.Core.Services;

public static class QrCodes
{
    /// <summary>
    /// Renders text as a PNG data URL.
    /// </summary>
    public static string ToPngDataUrl(string text, int pixelsPerModule = 8) => "data:image/png;base64," + Convert.ToBase64String(ToPng(text, pixelsPerModule));

    public static byte[] ToPng(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}

/// <summary>
/// App lifetime state shared by every screen (on MAUI each screen has its own Blazor scope).
/// </summary>
public sealed class AppState
{
    private readonly IDatabaseContainer _database;
    private readonly IDialogService _dialogs;
    private readonly KeyStore _keys;
    private readonly SignatureStore _signatures;
    private Task? _startup;

    public AppState(IDatabaseContainer database, IDialogService dialogs, KeyStore keys, SignatureStore signatures)
    {
        _database = database;
        _dialogs = dialogs;
        _keys = keys;
        _signatures = signatures;
    }

    /// <summary>
    /// Loads stored keys/signatures and the amiibo database, then offers a database update. Runs once.
    /// </summary>
    public Task EnsureStartedAsync() => _startup ??= StartAsync();

    private async Task StartAsync()
    {
        await _keys.EnsureLoadedAsync();
        try
        {
            await _signatures.EnsureLoadedAsync();
        }
        catch
        {
        }

        if (!_database.IsLoaded)
        {
            Exception? loadError = null;
            {
                await using var dialog = _database.LoadsRemotely ? _dialogs.ShowProgress("Loading Database", "Downloading amiibo data...") : null;
                try
                {
                    await _database.LoadData();
                }
                catch (Exception ex)
                {
                    loadError = ex;
                }
            }

            if (loadError != null)
            {
                await _dialogs.AlertAsync("Couldn't load the amiibo database", loadError.Message);
                if (_database.LoadsRemotely)
                {
                    return;
                }
            }
        }

        bool hasUpdate;
        try
        {
            hasUpdate = await _database.CheckUpdate();
        }
        catch
        {
            return;
        }

        if (!hasUpdate)
        {
            return;
        }

        var firstRun = !_database.IsLoaded;
        var update = firstRun || await _dialogs.ConfirmAsync("Database Update", "There is a new database version available, would you like to update?");
        if (update)
        {
            await UpdateDatabaseAsync();
        }
    }

    public async Task UpdateDatabaseAsync()
    {
        await using var dialog = _dialogs.ShowProgress("Updating Database", "Checking...");
        try
        {
            await _database.UpdateData(info =>
            {
                dialog.Update($"{info.Current} / {info.Total}\n{info.Message}", info.Total > 0 ? (double)info.Current / info.Total : null);
                return Task.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            await dialog.DisposeAsync();
            await _dialogs.AlertAsync("Database update failed", ex.Message);
        }
    }

}

/// <summary>
/// What this device can do, so screens only offer actions that will work.
/// </summary>
/// <summary>
/// Whether the main menu is the screen in front, so it can watch the NFC reader in the background.
/// The web sets it from the Home component's lifetime; MAUI from the root page appearing and disappearing.
/// </summary>
/// <summary>
/// Tells screens when one of them has been closed for good (popped or its sheet dismissed), as opposed to just covered
/// by another screen. MAUI raises it from the native page; on the web a component's disposal is the equivalent.
/// </summary>
public sealed class ScreenEvents
{
    public event EventHandler<AppRoute>? Closed;

    public void NotifyClosed(AppRoute route) => Closed?.Invoke(this, route);
}

public sealed class HomeScreenState
{
    public bool IsVisible { get; private set; }

    public event EventHandler? Changed;

    public void SetVisible(bool visible)
    {
        if (IsVisible == visible)
        {
            return;
        }

        IsVisible = visible;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class DeviceCapabilities
{
    private readonly IBleAdapter _ble;
    private readonly NfcService _nfc;
    private readonly IQrScanner _qr;
    private bool? _bluetooth;
    private bool? _camera;

    public DeviceCapabilities(IBleAdapter ble, NfcService nfc, IQrScanner qr)
    {
        _ble = ble;
        _nfc = nfc;
        _qr = qr;
    }

    /// <summary>
    /// A camera is present and QR scanning works here. False until <see cref="RefreshAsync"/> has checked, so the
    /// feature never appears and then disappears.
    /// </summary>
    public bool HasCamera => _camera ?? false;

    /// <summary>
    /// Bluetooth LE hardware (MAUI) or WebBluetooth with an adapter (browser).
    /// </summary>
    public bool HasBluetooth => _bluetooth ?? SafeBluetooth();

    /// <summary>
    /// At least one NFC module works here (phone NFC, a PC/SC or MFRC522 reader, Web Serial...).
    /// </summary>
    public bool HasNfc => _nfc.IsAvailable;

    /// <summary>
    /// Tags can be read or written somehow (NFC, or a Bluetooth puck/emulator).
    /// </summary>
    public bool CanUseTags => HasNfc || HasBluetooth;

    public event EventHandler? Changed;

    /// <summary>
    /// Runs the asynchronous checks (e.g. WebBluetooth's getAvailability) once.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            _bluetooth = _ble.IsAvailable && await _ble.CheckAvailabilityAsync();
        }
        catch
        {
            _bluetooth = false;
        }

        try
        {
            _camera = _qr.IsSupported && await _qr.HasCameraAsync();
        }
        catch
        {
            _camera = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool SafeBluetooth()
    {
        try
        {
            return _ble.IsAvailable;
        }
        catch
        {
            return false;
        }
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared services. The host must also register: IAppNavigator, IDialogService, IFileService,
    /// IAppStorage, IQrScanner, IFigureImageProvider, IDatabaseContainer, IBleAdapter, AppEnvironment, IAppPaths
    /// and any number of INfcProvider modules.
    /// </summary>
    public static IServiceCollection AddAmiiTagCore(this IServiceCollection services)
    {
        services.AddSingleton<KeyStore>();
        services.AddSingleton<SignatureStore>();
        services.AddSingleton<LockOnLibrary>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<TagStore>();
        services.AddSingleton<AmiiboLookup>();
        services.AddSingleton<GameCatalog>();
        services.AddSingleton<NfcService>();
        services.AddSingleton<HomeScreenState>();
        services.AddSingleton<ScreenEvents>();
        services.AddSingleton<DeviceCapabilities>();
        services.AddSingleton<AppState>();
        services.AddSingleton(sp =>
        {
            var keys = sp.GetRequiredService<KeyStore>();
            return new TagDeviceDetector(() => keys.RawKeys);
        });
        services.AddSingleton<BleDeviceManager>();
        services.AddSingleton<N2EliteService>();
        services.AddSingleton<FlipperEmulationService>();
        services.AddSingleton<TagActions>();

        // Per web view (each MAUI screen has its own), so the in-page menu shows on the screen that asked.
        services.AddScoped<ContextMenus>();
        return services;
    }
}
