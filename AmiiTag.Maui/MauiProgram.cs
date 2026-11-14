using AmiiTag.Bluetooth;
using AmiiTag.Core;
using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Services;
using AmiiTag.Maui.Data;
using AmiiTag.Maui.Services;
using AmiiTag.Nfc;
using Microsoft.Extensions.Logging;
#if ANDROID || IOS
using ZXing.Net.Maui.Controls;
#endif

namespace AmiiTag.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
#if DEBUG
            // Debug builds keep AmiiTag.log (cleared each launch) for inspecting reader traffic and errors: next to the
            // exe on Windows/Mac, in Documents on iOS, in the external files folder on Android.
            AmiiTag.Maui.Diagnostics.DebugFileLog.Start();
#endif
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

#if ANDROID || IOS
            builder.UseBarcodeReader();
#endif

            builder.Services.AddMauiBlazorWebView();
            builder.Services.AddScoped<HttpClient>(sp => new HttpClient());

            // Host services used by the shared Blazor components. All singletons: every native page has its own
            // BlazorWebView (and DI scope), but they share app state.
            builder.Services.AddSingleton<IAppPaths>(sp => new AppPaths());
            builder.Services.AddSingleton<AmiiboDatabaseContainer>();
            builder.Services.AddSingleton<IDatabaseContainer>(sp => sp.GetRequiredService<AmiiboDatabaseContainer>());
            builder.Services.AddSingleton<IAppNavigator, MauiNavigator>();
            builder.Services.AddSingleton<IDialogService, MauiDialogService>();
            builder.Services.AddSingleton<IFileService, MauiFileService>();
            builder.Services.AddSingleton<IAppStorage, MauiAppStorage>();
            builder.Services.AddSingleton<IFigureImageProvider, MauiFigureImageProvider>();
            builder.Services.AddSingleton<IQrScanner, CameraQrScanner>();
            builder.Services.AddSingleton<IBleAdapter, PluginBleAdapter>();
            // Flipper NFC Bridge: the fast bulk interface through WinUSB on Windows, HID otherwise (and as fallback).
#if WINDOWS
            builder.Services.AddSingleton<AmiiTag.Nfc.Flipper.IFlipperTransportProvider>(_ =>
                new CompositeFlipperTransportProvider(new WinUsbFlipperTransportProvider(), new HidFlipperTransportProvider()));
#elif MACCATALYST
            builder.Services.AddSingleton<AmiiTag.Nfc.Flipper.IFlipperTransportProvider, HidFlipperTransportProvider>();
#endif

            // Native context menus where they can open at the pointer (Mac uses the page's own menu).
#if WINDOWS
            builder.Services.AddSingleton<INativeContextMenu, AmiiTag.Maui.WinUI.WindowsContextMenu>();
#elif ANDROID || IOS
            builder.Services.AddSingleton<INativeContextMenu, SheetContextMenu>();
#endif
            builder.Services.AddSingleton(sp => new AppEnvironment()
            {
#if ANDROID
                Platform = AppEnvironment.PlatformType.Android
#elif IOS
                Platform = AppEnvironment.PlatformType.iOS
#elif MACCATALYST
                Platform = AppEnvironment.PlatformType.MacCatalyst
#elif WINDOWS
                Platform = AppEnvironment.PlatformType.Windows
#else
                Platform = AppEnvironment.PlatformType.Unknown
#endif
            });

            // NFC modules. Add more INfcProvider registrations to support other readers.
#if ANDROID
            builder.Services.AddSingleton<INfcProvider, AmiiTag.Maui.NfcProviders.AndroidNfcProvider>();
#elif IOS
            builder.Services.AddSingleton<INfcProvider, AmiiTag.Maui.NfcProviders.IosNfcProvider>();
#elif WINDOWS
            builder.Services.AddSingleton<INfcProvider, AmiiTag.Nfc.PcSc.PcscNfcProvider>();
            builder.Services.AddSingleton<AmiiTag.Nfc.Mfrc522.ISerialPortFactory, AmiiTag.Maui.NfcProviders.SystemSerialPortFactory>();
            builder.Services.AddSingleton<INfcProvider, AmiiTag.Nfc.Mfrc522.Mfrc522NfcProvider>();
#endif

            builder.Services.AddAmiiTagCore();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

            var app = builder.Build();

            AmiiTagEnvironment.ServiceProvider = app.Services;
            AmiiTagEnvironment.Type = AmiiTagEnvironment.EnvironmentType.Maui;

            return app;
        }
    }
}
