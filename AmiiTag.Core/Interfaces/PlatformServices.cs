using AmiiTag.AmiiboData;

namespace AmiiTag.Core.Interfaces;

public sealed record PickedFile(string Name, byte[] Data);

public interface IFileService
{
    /// <summary>
    /// Lets the user pick a file. Returns null if cancelled.
    /// </summary>
    /// <param name="title">A hint shown where supported.</param>
    /// <param name="extensions">Extensions like ".bin". Empty allows anything.</param>
    Task<PickedFile?> PickFileAsync(string title, IReadOnlyList<string> extensions);

    /// <summary>
    /// Lets the user pick one or more files. Returns an empty list if cancelled.
    /// </summary>
    Task<IReadOnlyList<PickedFile>> PickFilesAsync(string title, IReadOnlyList<string> extensions);

    /// <summary>
    /// Exports a file (share sheet on mobile, download in the browser).
    /// </summary>
    Task SaveFileAsync(string fileName, byte[] data, string contentType = "application/octet-stream");
}

/// <summary>
/// Small persistent key/value blob storage (files on MAUI, localStorage on the web).
/// </summary>
public interface IAppStorage
{
    Task<byte[]?> ReadAsync(string name);

    Task WriteAsync(string name, byte[] data);

    Task DeleteAsync(string name);

    /// <summary>
    /// Lists stored names starting with <paramref name="prefix"/>.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix);
}

public interface IQrScanner
{
    bool IsSupported { get; }

    /// <summary>
    /// True if a camera is actually present (the QR scan feature is hidden otherwise).
    /// </summary>
    Task<bool> HasCameraAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the camera and returns the text of the first QR code found, or null if cancelled.
    /// </summary>
    Task<string?> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns an amiibo figure into an image URL the current host can display.
/// </summary>
public interface IFigureImageProvider
{
    ValueTask<string?> GetImageUrlAsync(AmiiboFigure figure);
}

/// <summary>
/// Uses the image URI from the database as-is (remote URLs).
/// </summary>
public sealed class DirectFigureImageProvider : IFigureImageProvider
{
    public ValueTask<string?> GetImageUrlAsync(AmiiboFigure figure) => ValueTask.FromResult(figure.ImageUri?.ToString());
}
