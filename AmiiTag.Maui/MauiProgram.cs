using AmiiTag.Core;
using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using AmiiTag.Maui.Data;
using Microsoft.Extensions.Logging;

namespace AmiiTag.Maui
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            builder.Services.AddScoped(sp => new HttpClient());
            builder.Services.AddSingleton<IAppPaths>(sp => new AppPaths());
            builder.Services.AddSingleton<IDatabaseContainer>(sp => new AmiiboDatabaseContainer());
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

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            var app = builder.Build();

            AmiiTagEnvironment.ServiceProvider = app.Services;
            AmiiTagEnvironment.Type = AmiiTagEnvironment.EnvironmentType.Maui;

            return app;
        }
    }
}
