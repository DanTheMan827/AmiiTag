using AmiiTag.Core.Services;
using AmiiTag.Maui.Pages;
using AmiiTag.Maui.Services;

namespace AmiiTag.Maui
{
    public partial class App : Application
    {
        private readonly SyncedFigureImageFiles _figureImages;

        public App(SyncedFigureImageFiles figureImages)
        {
            _figureImages = figureImages;
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // Every screen is a native page hosting a shared Blazor component; home is the root of the stack.
            var home = new BlazorPage(AppRoutes.Home(), _figureImages, isRoot: true);
            NavigationPage.SetHasNavigationBar(home, false);
            var window = new Window(MauiNavigator.CreateNavigationPage(home)) { Title = "AmiiTag" };

            // Desktop windows start at a phone-friendly but roomy size, centred on the main display.
            if (DeviceInfo.Idiom == DeviceIdiom.Desktop)
            {
                const double width = 1024;
                const double height = 720;
                window.Width = width;
                window.Height = height;

                var display = DeviceDisplay.Current.MainDisplayInfo;
                if (display.Density > 0 && display.Width > 0 && display.Height > 0)
                {
                    // MainDisplayInfo is in pixels; window coordinates are in device independent units.
                    var screenWidth = display.Width / display.Density;
                    var screenHeight = display.Height / display.Density;
                    window.X = Math.Max(0, (screenWidth - width) / 2);
                    window.Y = Math.Max(0, (screenHeight - height) / 2);
                }
            }

            return window;
        }
    }
}
