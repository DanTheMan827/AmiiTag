using System.Text;
using AmiiTag.AmiiboData;
using AmiiTag.WebAssembly.Data;
using AmiiTag.WebAssembly.Services;
using Bunit;
using AmiiTag.Core;
using AmiiTag.Core.Components;
using AmiiTag.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace AmiiTag.Core.Tests;

public sealed class WebDatabaseTests : BunitContext
{
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("{\"database/amiibo.json\":\"dbhash\",\"database/games_info.json\":\"gameshash\",\"images/icon_00000000-00000002.png\":\"imagehash\"}");
    private static readonly byte[] Figures = Encoding.UTF8.GetBytes("{\"amiibos\":{\"0x0000000000000002\":{\"name\":\"Test\"}}}");
    private static readonly byte[] Empty = Encoding.UTF8.GetBytes("{}");
    private static string Url(JSRuntimeInvocation invocation) => (string)invocation.Arguments[0]!;
    private static bool Cached(JSRuntimeInvocation invocation) => (bool)invocation.Arguments[1]!;
    private const string ImageUrl = "icon_00000000-00000002.png?imagehash";

    private void Setup(bool cacheOnly)
    {
        JSInterop.Setup<byte[]>("amiitag.assets.readManifest", i => Cached(i) == cacheOnly).SetResult(Manifest);
        JSInterop.Setup<byte[]>("amiitag.assets.readFile", i => Cached(i) == cacheOnly && Url(i).Contains("/amiibo.json")).SetResult(Figures);
        JSInterop.Setup<byte[]>("amiitag.assets.readFile", i => Cached(i) == cacheOnly && Url(i).Contains("games_info.json")).SetResult(Empty);
        JSInterop.SetupVoid("amiitag.assets.commitManifest", _ => true).SetVoidResult();
        JSInterop.Setup<string[]>("amiitag.assets.uncached", _ => true).SetResult([$"https://dantheman827.github.io/UnofficialAmiiboDB/images/{ImageUrl}"]);
        JSInterop.Setup<int>("amiitag.assets.cacheFiles", _ => true).SetResult(0);
        JSInterop.Setup<int>("amiitag.assets.retain", _ => true).SetResult(0);
        JSInterop.Setup<byte[]?>("amiitag.assets.readSnapshot", _ => true).SetResult(null);
        JSInterop.SetupVoid("amiitag.assets.writeSnapshot", _ => true).SetVoidResult();
    }

    private string[] RetainedHashes() => (string[])Assert.Single(JSInterop.Invocations["amiitag.assets.retain"]).Arguments[0]!;

    [Fact]
    public async Task FreshImportCommitsManifests_CachesUncachedImages_AndKeepsOnlyListedHashes()
    {
        Setup(false);
        var database = new AmiiboDatabase();
        var container = new AmiiboDatabaseContainer(database, JSInterop.JSRuntime);
        await container.LoadData();
        Assert.True(container.IsLoaded); Assert.Single(database.Figures);
        Assert.Equal(2, JSInterop.Invocations["amiitag.assets.commitManifest"].Count);
        foreach (var invocation in JSInterop.Invocations["amiitag.assets.commitManifest"])
            Assert.EndsWith("/sha1sum.json", (string)invocation.Arguments[0]!);
        var uncached = Assert.Single(JSInterop.Invocations["amiitag.assets.uncached"]);
        Assert.EndsWith(ImageUrl, Assert.Single((string[])uncached.Arguments[0]!));
        var download = Assert.Single(JSInterop.Invocations["amiitag.assets.cacheFiles"]);
        Assert.EndsWith(ImageUrl, Assert.Single((string[])download.Arguments[0]!));
        Assert.Equal(new[] { "dbhash", "gameshash", "imagehash" }, RetainedHashes().Order());
    }

    [Fact]
    public async Task DatabaseFilesAreReadByTheirHashedUrls()
    {
        Setup(false);
        await new AmiiboDatabaseContainer(new AmiiboDatabase(), JSInterop.JSRuntime).LoadData();
        var files = JSInterop.Invocations["amiitag.assets.readFile"].Select(Url).ToList();
        Assert.Contains(files, url => url.EndsWith("/database/amiibo.json?dbhash"));
        Assert.Contains(files, url => url.EndsWith("/database/games_info.json?gameshash"));
    }

    [Fact]
    public async Task NetworkFailureReimportsTheLastGoodSnapshotFromIndexedDb()
    {
        Setup(true);
        JSInterop.Setup<byte[]>("amiitag.assets.readManifest", i => !Cached(i)).SetException(new JSException("offline"));
        JSInterop.Setup<byte[]>("amiitag.assets.readFile", i => !Cached(i)).SetException(new JSException("offline"));
        var database = new AmiiboDatabase();
        var container = new AmiiboDatabaseContainer(database, JSInterop.JSRuntime);
        await container.LoadData();
        Assert.True(container.IsLoaded); Assert.Single(database.Figures);
        Assert.Empty(JSInterop.Invocations["amiitag.assets.commitManifest"]);
        Assert.Empty(JSInterop.Invocations["amiitag.assets.cacheFiles"]);
        Assert.Contains("imagehash", RetainedHashes());
    }

    [Fact]
    public async Task FailedUpdatePreservesTheAlreadyLoadedDatabase()
    {
        Setup(false);
        var database = new AmiiboDatabase();
        var container = new AmiiboDatabaseContainer(database, JSInterop.JSRuntime);
        await container.LoadData(); var original = Assert.Single(database.Figures).Value;
        JSInterop.Setup<byte[]>("amiitag.assets.readManifest", _ => true).SetException(new JSException("offline and no cache"));
        JSInterop.Setup<byte[]>("amiitag.assets.readFile", _ => true).SetException(new JSException("offline and no cache"));
        await Assert.ThrowsAsync<JSException>(() => container.UpdateData());
        Assert.True(container.IsLoaded); Assert.Same(original, Assert.Single(database.Figures).Value);
    }

    [Fact]
    public async Task CancelledImportDoesNotStartAnOfflineRetry()
    {
        JSInterop.Setup<byte[]>("amiitag.assets.readManifest", _ => true).SetException(new OperationCanceledException());
        var container = new AmiiboDatabaseContainer(new AmiiboDatabase(), JSInterop.JSRuntime);
        await Assert.ThrowsAsync<OperationCanceledException>(() => container.LoadData());
        Assert.Single(JSInterop.Invocations["amiitag.assets.readManifest"]);
        Assert.False(container.IsLoaded);
    }

    [Fact]
    public async Task ImportSavesASnapshot_ThatTheNextLoadUsesInsteadOfParsing()
    {
        Setup(false);
        var first = new AmiiboDatabase();
        await new AmiiboDatabaseContainer(first, JSInterop.JSRuntime).LoadData();
        var write = Assert.Single(JSInterop.Invocations["amiitag.assets.writeSnapshot"]);
        var key = (string)write.Arguments[0]!;
        var bytes = (byte[])write.Arguments[1]!;

        using var next = new BunitContext();
        next.JSInterop.Setup<byte[]>("amiitag.assets.readManifest", _ => true).SetResult(Manifest);
        next.JSInterop.Setup<byte[]?>("amiitag.assets.readSnapshot", i => (string)i.Arguments[0]! == key).SetResult(bytes);
        next.JSInterop.Setup<string[]>("amiitag.assets.uncached", _ => true).SetResult([]);
        next.JSInterop.Setup<int>("amiitag.assets.retain", _ => true).SetResult(0);
        next.JSInterop.SetupVoid("amiitag.assets.commitManifest", _ => true).SetVoidResult();
        var second = new AmiiboDatabase();
        await new AmiiboDatabaseContainer(second, next.JSInterop.JSRuntime).LoadData();
        Assert.Empty(next.JSInterop.Invocations["amiitag.assets.readFile"]);
        var original = Assert.Single(first.Figures);
        var loaded = Assert.Single(second.Figures);
        Assert.Equal(original.Key, loaded.Key);
        Assert.Equal(original.Value.Name, loaded.Value.Name);
        Assert.Equal(original.Value.ImageUri, loaded.Value.ImageUri);
    }

    [Fact]
    public void SnapshotRoundTripsFiguresAndSharedGames()
    {
        var database = AmiiboDatabaseSnapshot.TryRead(AmiiboDatabaseSnapshot.Write(new AmiiboDatabase()));
        Assert.NotNull(database);
        Assert.Empty(database!.Figures);
        Assert.Null(AmiiboDatabaseSnapshot.TryRead([1, 2, 3]));
        Assert.Null(AmiiboDatabaseSnapshot.TryRead([]));
    }

    [Fact]
    public async Task UpdateReportsProgress()
    {
        Setup(false);
        var reports = new List<ProgressInfo>();
        await new AmiiboDatabaseContainer(new AmiiboDatabase(), JSInterop.JSRuntime)
            .UpdateData(info => { reports.Add(info); return Task.CompletedTask; });
        Assert.NotEmpty(reports);
        Assert.All(reports, info => Assert.True(info.Total > 0));
        Assert.Contains(reports, info => info.Message.StartsWith("Downloading images"));
    }

    [Fact]
    public async Task LateCachedImageCompletionDoesNotReplaceTheCurrentFigure()
    {
        var images = new DelayedImages();
        Services.AddSingleton<IFigureImageProvider>(images);
        var first = new AmiiboFigure(); var second = new AmiiboFigure();
        var cut = Render<FigureImage>(p => p.Add(c => c.Figure, first));
        cut.Render(p => p.Add(c => c.Figure, second));
        images.Pending[second].SetResult("blob:second");
        cut.WaitForAssertion(() => Assert.Equal("blob:second", cut.Find("img").GetAttribute("src")));
        await cut.InvokeAsync(() => images.Pending[first].SetResult("blob:first"));
        cut.WaitForAssertion(() => Assert.Equal("blob:second", cut.Find("img").GetAttribute("src")));
    }

    private sealed class DelayedImages : IFigureImageProvider
    {
        public Dictionary<AmiiboFigure, TaskCompletionSource<string?>> Pending { get; } = [];
        public ValueTask<string?> GetImageUrlAsync(AmiiboFigure figure)
        {
            var task = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(figure, task);
            return new ValueTask<string?>(task.Task);
        }
    }

    [Fact]
    public async Task WebStorageUsesBinaryInterop_AndFigureProviderUsesBlobUrl()
    {
        var bytes = new byte[] { 0, 128, 255 };
        JSInterop.Setup<byte[]?>("amiitag.storage.read", "settings").SetResult(bytes);
        JSInterop.SetupVoid("amiitag.storage.write", _ => true).SetVoidResult();
        var storage = new WebAppStorage(JSInterop.JSRuntime);
        Assert.Equal(bytes, await storage.ReadAsync("settings"));
        await storage.WriteAsync("settings", bytes);
        Assert.IsType<byte[]>(Assert.Single(JSInterop.Invocations["amiitag.storage.write"]).Arguments[1]);
        Setup(false);
        var database = new AmiiboDatabase(); await new AmiiboDatabaseContainer(database, JSInterop.JSRuntime).LoadData();
        JSInterop.Setup<string?>("amiitag.assets.imageUrl", _ => true).SetResult("blob:https://app/image");
        JSInterop.SetupVoid("amiitag.assets.releaseImageUrls").SetVoidResult();
        await using var provider = new WebFigureImageProvider(JSInterop.JSRuntime);
        Assert.Equal("blob:https://app/image", await provider.GetImageUrlAsync(Assert.Single(database.Figures).Value));
    }
}
