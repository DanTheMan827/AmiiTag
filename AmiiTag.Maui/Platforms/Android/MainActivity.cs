using Android.App;
using Android.Content.PM;
using Android.OS;

namespace AmiiTag.Maui
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            if (Window != null)
            {
                AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(Window, false);
                Window.AddFlags(Android.Views.WindowManagerFlags.DrawsSystemBarBackgrounds);
                if (OperatingSystem.IsAndroidVersionAtLeast(29)) Window.NavigationBarContrastEnforced = false;
                if (OperatingSystem.IsAndroidVersionAtLeast(28) && Window.Attributes is { } attributes)
                {
                    attributes.LayoutInDisplayCutoutMode = OperatingSystem.IsAndroidVersionAtLeast(30)
                        ? Android.Views.LayoutInDisplayCutoutMode.Always : Android.Views.LayoutInDisplayCutoutMode.ShortEdges;
                    Window.Attributes = attributes;
                }
            }
        }
    }
}
