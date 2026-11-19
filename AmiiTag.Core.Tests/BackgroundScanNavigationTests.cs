using AmiiTag.Core.Services;
using AmiiTag.Core.Services.Web;
using AmiiTag.Tags;
using Xunit;

namespace AmiiTag.Core.Tests;

public sealed class BackgroundScanNavigationTests
{
    [Fact]
    public async Task QueuedScanFromClosedHomeDoesNotShowDialogOrOpenTag()
    {
        var dialogs = new BlazorDialogService();
        var actions = Actions(dialogs);
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        await actions.ShowScannedTagAsync(new AmiiboV3Dump(new byte[AmiiboV3Dump.ImageLength]),
            automatic: true, cancellationToken: lifetime.Token);
        Assert.Empty(dialogs.Dialogs);
    }

    [Fact]
    public async Task LeavingHomeWhileWarningIsOpenDoesNotNavigateWhenWarningCloses()
    {
        var dialogs = new BlazorDialogService();
        var actions = Actions(dialogs);
        using var lifetime = new CancellationTokenSource();
        var pending = actions.ShowScannedTagAsync(new AmiiboV3Dump(new byte[AmiiboV3Dump.ImageLength]),
            automatic: true, cancellationToken: lifetime.Token);
        var warning = Assert.Single(dialogs.Dialogs);
        Assert.False(pending.IsCompleted);
        lifetime.Cancel(); // Home is no longer visible / has been disposed.
        dialogs.Complete(warning, null);
        await pending;
        Assert.Empty(dialogs.Dialogs);
    }

    // A cancelled scan must finish before reaching tag storage or navigation.
    // Leaving those dependencies absent makes an accidental OpenTag fail here.
    private static TagActions Actions(BlazorDialogService dialogs) =>
        new(null!, null!, dialogs, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
}
