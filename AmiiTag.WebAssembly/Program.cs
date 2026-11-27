using AmiiTag.AmiiboData;
using AmiiTag.Bluetooth;
using AmiiTag.Core;
using AmiiTag.Core.Components;
using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using AmiiTag.Core.Services;
using AmiiTag.Core.Services.Web;
using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;
using AmiiTag.WebAssembly.Data;
using AmiiTag.WebAssembly.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

#if DEBUG
// Debug builds keep a log of this page load; amiitag.downloadDebugLog() in the console saves it as AmiiTag.log.
AmiiTag.WebAssembly.Services.DebugLog.Start();
#endif
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Host services used by the shared Blazor components.
builder.Services.AddSingleton<AmiiboDatabase>();
builder.Services.AddSingleton<IDatabaseContainer, AmiiboDatabaseContainer>();
builder.Services.AddSingleton<IAppPaths>(sp => new AppPaths() { BaseAddress = builder.HostEnvironment.BaseAddress });
builder.Services.AddSingleton(sp => new AppEnvironment()
{
    Platform = AppEnvironment.PlatformType.WebAssembly,
    IsIosBrowser = InvokeCheck(sp, "amiitag.isIosDevice"),
    IsMobileSafari = InvokeCheck(sp, "amiitag.isMobileSafari"),
    IsIosStandalone = InvokeCheck(sp, "amiitag.isIosStandalone"),
    HasWebBluetooth = InvokeCheck(sp, "amiitag.bluetooth.isSupported"),
});
builder.Services.AddSingleton<BlazorDialogService>();
builder.Services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<BlazorDialogService>());
builder.Services.AddSingleton<IAppNavigator, BrowserNavigator>();
builder.Services.AddSingleton<IFileService, WebFileService>();
builder.Services.AddSingleton<IAppStorage, WebAppStorage>();
builder.Services.AddSingleton<IQrScanner, WebQrScanner>();
builder.Services.AddSingleton<IFigureImageProvider, WebFigureImageProvider>();
builder.Services.AddSingleton<IBleAdapter, WebBluetoothAdapter>();

// NFC: browsers can't send raw NFC commands, but an MFRC522 on a USB serial adapter can be driven over Web Serial.
builder.Services.AddSingleton<ISerialPortFactory, WebSerialPortFactory>();
// One choice in Settings, with the chip detected on connection (PN532, else MFRC522).
builder.Services.AddSingleton<Mfrc522NfcProvider>();
builder.Services.AddSingleton<AmiiTag.Nfc.Pn532.Pn532NfcProvider>();
builder.Services.AddSingleton<INfcProvider>(sp => new AutoDetectSerialNfcProvider(
    sp.GetRequiredService<Mfrc522NfcProvider>(), sp.GetRequiredService<AmiiTag.Nfc.Pn532.Pn532NfcProvider>()));

builder.Services.AddAmiiTagCore();

var host = builder.Build();

// Browser feature checks for wording messages (amiitag.js loads before Blazor starts).
static bool InvokeCheck(IServiceProvider services, string function)
{
    try
    {
        return services.GetRequiredService<Microsoft.JSInterop.IJSRuntime>() is Microsoft.JSInterop.IJSInProcessRuntime js && js.Invoke<bool>(function);
    }
    catch
    {
        return false;
    }
}

AmiiTagEnvironment.ServiceProvider = host.Services;
AmiiTagEnvironment.Type = AmiiTagEnvironment.EnvironmentType.WebAssembly;

await host.RunAsync();
