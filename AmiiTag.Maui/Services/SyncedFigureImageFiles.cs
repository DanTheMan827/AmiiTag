using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;

namespace AmiiTag.Maui.Services;

/// <summary>
/// Mounts downloaded figure icons under the WebView's own origin. Only icon PNGs are served; the database,
/// keys and other app data are outside this namespace. Files are opened by the static asset pipeline on demand.
/// </summary>
public sealed class SyncedFigureImageFiles : IFileProvider
{
    public const string UrlPrefix = "figure-images/";
    private readonly Func<IEnumerable<string>> _sourceFolders;

    /// <param name="sourceFolders">The databases' folders, asked on each request (the list of databases can change).</param>
    public SyncedFigureImageFiles(Func<IEnumerable<string>> sourceFolders)
    {
        _sourceFolders = sourceFolders;
    }

    public string? GetImageUrl(string fileName)
    {
        if (!IsIconName(fileName)) return null;
        var path = UrlPrefix + fileName;
        var file = GetFileInfo(path);
        // A changed sync produces a new src, rather than leaving an already-rendered image on old bytes.
        return file.Exists ? $"/{path}?v={file.LastModified.UtcTicks:x}-{file.Length:x}" : null;
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        // BlazorWebView passes the relative request URI, including any cache-version query string.
        var path = subpath.Split('?', 2)[0].TrimStart('/');
        if (!path.StartsWith(UrlPrefix, StringComparison.Ordinal)) return new NotFoundFileInfo(subpath);
        var name = path[UrlPrefix.Length..];
        if (!IsIconName(name)) return new NotFoundFileInfo(subpath);

        foreach (var folder in _sourceFolders())
        {
            var file = new FileInfo(Path.Combine(folder, "images", name));
            if (file.Exists) return new PhysicalFileInfo(file);
        }
        return new NotFoundFileInfo(name);
    }

    private static bool IsIconName(string name)
    {
        if (name.Length != 26 || !name.StartsWith("icon_", StringComparison.Ordinal) ||
            name[13] != '-' || !name.EndsWith(".png", StringComparison.Ordinal)) return false;
        for (var i = 5; i < 22; i++)
        {
            if (i != 13 && !Uri.IsHexDigit(name[i])) return false;
        }
        return true;
    }

    // No directory listing or file watchers are needed. Requests see files synced after the WebView was created,
    // even when none of the image directories existed at startup.
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
}
