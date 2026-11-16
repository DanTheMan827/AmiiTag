using AmiiTag.Maui.Services;
using Microsoft.Extensions.FileProviders;

namespace AmiiTag.Tests;

public class FigureImageFileTests : IDisposable
{
    private const string Icon = "icon_00000000-00340103.png";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AmiiTagImageTests-" + Guid.NewGuid().ToString("N"));
    private string Source(string name) => Path.Combine(_root, name);
    private string SaveIcon(string source, byte[] bytes)
    {
        var images = Path.Combine(Source(source), "images");
        Directory.CreateDirectory(images);
        var path = Path.Combine(images, Icon);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task StaticUrlStreamsTheOriginalImageBytes()
    {
        SaveIcon("official", [0x89, 0x50, 0x4E, 0x47]);
        var files = new SyncedFigureImageFiles([Source("official")]);
        var url = files.GetImageUrl(Icon)!;
        Assert.StartsWith("/figure-images/" + Icon + "?v=", url);
        Assert.DoesNotContain("base64", url);
        var info = files.GetFileInfo(url); // BlazorWebView includes the query in its file-provider lookup.
        Assert.Equal(Icon, info.Name);
        await using var stream = info.CreateReadStream();
        using var data = new MemoryStream();
        await stream.CopyToAsync(data);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, data.ToArray());
    }

    [Fact]
    public void ImagesSyncedAfterTheWebViewWasCreatedBecomeAvailable()
    {
        var files = new SyncedFigureImageFiles([Source("official")]);
        Assert.Null(files.GetImageUrl(Icon));
        Assert.False(Directory.Exists(Source("official")));
        SaveIcon("official", [1, 2, 3]);
        Assert.NotNull(files.GetImageUrl(Icon));
        Assert.True(files.GetFileInfo("figure-images/" + Icon).Exists);
    }

    [Fact]
    public void UpdatedImagesProduceANewUrlWithoutAnEncodedImageCache()
    {
        var path = SaveIcon("official", [1, 2]);
        var files = new SyncedFigureImageFiles([Source("official")]);
        var original = files.GetImageUrl(Icon);
        File.WriteAllBytes(path, [3, 4, 5]);
        Assert.NotEqual(original, files.GetImageUrl(Icon));
        using var stream = files.GetFileInfo(files.GetImageUrl(Icon)!).CreateReadStream();
        Assert.Equal(3, stream.ReadByte());
    }

    [Fact]
    public void SourcePriorityAndMissingImageFallbackArePreserved()
    {
        var files = new SyncedFigureImageFiles([Source("official"), Source("unofficial")]);
        Assert.Null(files.GetImageUrl(Icon));
        SaveIcon("unofficial", [2]);
        Assert.Contains("unofficial", files.GetFileInfo("figure-images/" + Icon).PhysicalPath);
        SaveIcon("official", [1]);
        Assert.Contains("official", files.GetFileInfo("figure-images/" + Icon).PhysicalPath);
        Assert.Equal(Path.Combine(Source("official"), "images", Icon), files.GetFileInfo("figure-images/" + Icon).PhysicalPath);
    }

    [Theory]
    [InlineData("../key_retail.bin")]
    [InlineData("figure-images/../database/amiibo.json")]
    [InlineData("figure-images/../../key_retail.bin")]
    [InlineData("figure-images/..\\key_retail.bin")]
    [InlineData("figure-images/%2e%2e/key_retail.bin")]
    [InlineData("figure-images/database.json")]
    [InlineData("figure-images/images/icon_00000000-00340103.png")]
    public void OnlyFlatIconPngPathsAreExposed(string request)
    {
        SaveIcon("official", [1]);
        var files = new SyncedFigureImageFiles([Source("official")]);
        Assert.False(files.GetFileInfo(request).Exists);
        Assert.False(files.GetDirectoryContents("figure-images").Exists);
    }

    [Fact]
    public void NormalBlazorAssetsStillResolveThroughTheCompositeProvider()
    {
        var images = new SyncedFigureImageFiles([Source("official")]);
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "host page");
        using var assets = new PhysicalFileProvider(webRoot);
        var combined = new CompositeFileProvider(images, assets);
        Assert.True(combined.GetFileInfo("index.html").Exists);
        Assert.False(images.GetFileInfo("index.html").Exists);
        SaveIcon("official", [1]);
        Assert.True(combined.GetFileInfo(images.GetImageUrl(Icon)!).Exists);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
