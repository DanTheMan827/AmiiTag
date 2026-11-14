using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.FileProviders;

namespace AmiiTag.Maui.Pages;

/// <summary>
/// The BlazorWebView every screen uses. On Windows it serves wwwroot from the folder the app's files are in
/// (<see cref="AppContext.BaseDirectory"/>): next to the exe normally, or the folder a single file exe extracts its
/// content to, which MAUI's own lookup (the exe's folder) wouldn't find. The plain files are served, never the .br/.gz
/// copies a publish can map them to.
/// </summary>
public sealed class AppBlazorWebView : BlazorWebView
{
    public override IFileProvider CreateFileProvider(string contentRootDir)
    {
#if WINDOWS
        var root = Path.IsPathRooted(contentRootDir) ? contentRootDir : Path.Combine(AppContext.BaseDirectory, contentRootDir);
        if (Directory.Exists(root) && File.Exists(Path.Combine(root, "index.html")))
        {
            return new PhysicalFileProvider(root);
        }
#endif
        return base.CreateFileProvider(contentRootDir);
    }
}
