using System.Collections.Concurrent;
using AmiiTag.AmiiboData;
using AmiiTag.Core.Interfaces;
using AmiiTag.Maui.Data;

namespace AmiiTag.Maui.Services;

public sealed class MauiFileService : IFileService
{
    public async Task<PickedFile?> PickFileAsync(string title, IReadOnlyList<string> extensions)
    {
        var result = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title }));
        if (result == null)
        {
            return null;
        }

        await using var stream = await result.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return new PickedFile(result.FileName, memory.ToArray());
    }

    public async Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> extensions)
    {
        var results = await MainThread.InvokeOnMainThreadAsync(() => FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = title }));
        var files = new List<PickedFile>();
        foreach (var result in results ?? Enumerable.Empty<FileResult?>())
        {
            if (result == null)
            {
                continue;
            }

            await using var stream = await result.OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            files.Add(new PickedFile(result.FileName, memory.ToArray()));
        }

        return files;
    }

    public async Task SaveFileAsync(string fileName, byte[] data, string contentType = "application/octet-stream")
    {
#if WINDOWS
        // Desktop: the system's Save dialog.
        await MainThread.InvokeOnMainThreadAsync(() => SaveWithPickerAsync(fileName, data));
#else
        var folder = Path.Combine(FileSystem.CacheDirectory, "exports");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        await File.WriteAllBytesAsync(path, data);

#if MACCATALYST
        // Desktop: the system's Save panel (an export picker on Mac Catalyst).
        await MainThread.InvokeOnMainThreadAsync(() => ExportWithPickerAsync(path));
#else
        // Phones export through the share sheet (Save to Files, AirDrop, Drive...).
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File = new ShareFile(path, contentType),
        }));
#endif
#endif
    }

#if WINDOWS
    private static async Task SaveWithPickerAsync(string fileName, byte[] data)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".bin";
        }

        var picker = new global::Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(fileName),
            SuggestedStartLocation = global::Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(DescribeFileType(extension), new List<string> { extension });

        // Unpackaged WinUI apps must tell the picker which window owns it.
        if (Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        }

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            await File.WriteAllBytesAsync(file.Path, data);
        }
    }

    private static string DescribeFileType(string extension) => extension.ToLowerInvariant() switch
    {
        ".bin" => "Tag dump",
        ".txt" => "Text",
        ".zip" => "Zip archive",
        ".png" => "PNG image",
        _ => $"{extension.TrimStart('.').ToUpperInvariant()} file",
    };
#endif

#if MACCATALYST
    private static Task ExportWithPickerAsync(string path)
    {
        var done = new TaskCompletionSource();
        var picker = new UIKit.UIDocumentPickerViewController([Foundation.NSUrl.FromFilename(path)], asCopy: true);
        picker.DidPickDocumentAtUrls += (_, _) => done.TrySetResult();
        picker.WasCancelled += (_, _) => done.TrySetResult();

        var presenter = Platform.GetCurrentUIViewController();
        if (presenter == null)
        {
            return Task.CompletedTask;
        }

        presenter.PresentViewController(picker, true, null);
        return done.Task;
    }
#endif
}

/// <summary>
/// Stores blobs as files under the app's user data folder (Documents on iOS, so key_retail.bin can be dropped in with Files).
/// </summary>
public sealed class MauiAppStorage : IAppStorage
{
    private readonly string _root;

    public MauiAppStorage(IAppPaths paths)
    {
        _root = paths.UserData;
    }

    private string Resolve(string name)
    {
        var path = Path.GetFullPath(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(_root), StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid storage name.", nameof(name));
        }

        return path;
    }

    public async Task<byte[]?> ReadAsync(string name)
    {
        var path = Resolve(name);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
    }

    public async Task WriteAsync(string name, byte[] data)
    {
        var path = Resolve(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, data);
    }

    public Task DeleteAsync(string name)
    {
        var path = Resolve(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix)
    {
        var directoryPart = prefix.Contains('/') ? prefix[..prefix.LastIndexOf('/')] : string.Empty;
        var directory = Resolve(directoryPart);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var names = Directory.EnumerateFiles(directory)
            .Select(file => Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }
}

/// <summary>
/// Serves synced images as data URLs, since a BlazorWebView can't load arbitrary local files.
/// Falls back to the remote URL when the image hasn't been downloaded.
/// </summary>
public sealed class MauiFigureImageProvider : IFigureImageProvider
{
    private readonly AmiiboDatabaseContainer _database;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public MauiFigureImageProvider(AmiiboDatabaseContainer database)
    {
        _database = database;
    }

    public async ValueTask<string?> GetImageUrlAsync(AmiiboFigure figure)
    {
        var fileName = $"icon_{figure.Id.Head}-{figure.Id.Tail}.png";
        if (_cache.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        foreach (var folder in _database.SourceFolders)
        {
            var path = Path.Combine(folder, "images", fileName);
            if (File.Exists(path))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var url = "data:image/png;base64," + Convert.ToBase64String(bytes);
                _cache[fileName] = url;
                return url;
            }
        }

        return _database.GetRemoteImageUrl(fileName);
    }
}
