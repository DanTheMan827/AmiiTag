using AmiiTag.Core;
using AmiiTag.Core.Components;
using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using AmiiTag.WebAssembly.Data;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<Routes>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IDatabaseContainer>(sp => new AmiiboDatabaseContainer());
builder.Services.AddScoped<IAppPaths>(sp => new AppPaths() { BaseAddress = builder.HostEnvironment.BaseAddress });
builder.Services.AddSingleton(sp => new AppEnvironment()
{
    Platform = AppEnvironment.PlatformType.WebAssembly
});

var host = builder.Build();

AmiiTagEnvironment.ServiceProvider = host.Services;

await host.RunAsync();
