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
    Platform = AppEnvironment.PlatformType.WebAssembly
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
builder.Services.AddSingleton<INfcProvider, Mfrc522NfcProvider>();
builder.Services.AddSingleton<INfcProvider, AmiiTag.Nfc.Pn532.Pn532NfcProvider>();

builder.Services.AddAmiiTagCore();

var host = builder.Build();

AmiiTagEnvironment.ServiceProvider = host.Services;
AmiiTagEnvironment.Type = AmiiTagEnvironment.EnvironmentType.WebAssembly;

await host.RunAsync();
