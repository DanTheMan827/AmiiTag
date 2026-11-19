using AmiiTag.Core.Components;
using AmiiTag.Core.Services;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace AmiiTag.Core.Tests;

public class PageChromeTests
{
    [Fact]
    public async Task SettingsStayWithTheirOwnPage()
    {
        var first = new PageChromeState();
        var second = new PageChromeState();
        var firstCalls = 0;
        var secondCalls = 0;
        first.SetSettings(new object(), () => { firstCalls++; return Task.CompletedTask; });
        second.SetSettings(new object(), () => { secondCalls++; return Task.CompletedTask; });
        await first.OpenSettingsAsync();
        Assert.Equal(1, firstCalls);
        Assert.Equal(0, secondCalls);
    }

    [Fact]
    public async Task UnsupportedHostCannotPullToRefresh()
    {
        var chrome = new PageChromeState(false);
        var calls = 0;
        chrome.SetRefresh(this, () => { calls++; return Task.CompletedTask; });
        Assert.False(chrome.CanRefresh);
        await chrome.RefreshAsync();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NestedScrollerGuardPausesGestureWithoutLosingUpdateAction()
    {
        var chrome = new PageChromeState(true);
        var calls = 0;
        chrome.SetRefresh(this, () => { calls++; return Task.CompletedTask; });
        chrome.SetPullBlocked(true);
        await chrome.RefreshAsync();
        Assert.Equal(0, calls);
        chrome.SetPullBlocked(false);
        await chrome.RefreshAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RefreshDoesNotOverlap_AndRecoversAfterFailure()
    {
        var chrome = new PageChromeState(true);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        chrome.SetRefresh(this, async () => { calls++; await completion.Task; });
        var pending = chrome.RefreshAsync();
        await chrome.RefreshAsync();
        Assert.Equal(1, calls);
        Assert.True(chrome.IsRefreshing);
        completion.SetResult();
        await pending;
        Assert.False(chrome.IsRefreshing);
        chrome.SetRefresh(this, () => throw new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidOperationException>(chrome.RefreshAsync);
        Assert.False(chrome.IsRefreshing);
        chrome.SetRefresh(this, () => { calls++; return Task.CompletedTask; });
        await chrome.RefreshAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisposingOldOwnerDoesNotRemoveNewCallbacks()
    {
        var chrome = new PageChromeState(true);
        var oldOwner = new object();
        var newOwner = new object();
        var calls = 0;
        chrome.SetRefresh(oldOwner, () => Task.CompletedTask);
        chrome.SetSettings(oldOwner, () => Task.CompletedTask);
        chrome.SetRefresh(newOwner, () => { calls++; return Task.CompletedTask; });
        chrome.SetSettings(newOwner, () => { calls++; return Task.CompletedTask; });
        chrome.Remove(oldOwner);
        await chrome.RefreshAsync();
        await chrome.OpenSettingsAsync();
        Assert.Equal(2, calls);
        chrome.Remove(newOwner);
        Assert.False(chrome.CanRefresh);
        Assert.False(chrome.HasSettings);
    }

    [Fact]
    public async Task RefreshComponentRegistersOnRendererAndUnregistersOnDisposal()
    {
        using var context = new BunitContext();
        var chrome = new PageChromeState(true);
        var calls = 0;
        var cut = context.Render<CascadingValue<PageChromeState>>(p => p
            .Add(c => c.Value, chrome)
            .AddChildContent<PageRefreshAction>(action => action.Add(c => c.OnRefresh, () => { calls++; })));
        Assert.True(chrome.CanRefresh);
        await chrome.RefreshAsync();
        Assert.Equal(1, calls);
        cut.Render(p => p.Add(c => c.Value, chrome).AddChildContent(""));
        cut.WaitForAssertion(() => Assert.False(chrome.CanRefresh));
    }
    [Fact]
    public async Task RepeatedNativeBackDoesNotPopTwoFoldersWhileReadIsPending()
    {
        var chrome = new PageChromeState();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        chrome.SetBack(this, () => { calls++; return completion.Task; });
        var pending = chrome.TryGoBackAsync();
        Assert.True(await chrome.TryGoBackAsync());
        Assert.Equal(1, calls);
        completion.SetResult(true);
        Assert.True(await pending);
        chrome.SetBack(this, () => Task.FromResult(false));
        Assert.False(await chrome.TryGoBackAsync());
    }

    [Fact]
    public async Task BackCallbackCleanupAndFailuresDoNotTrapFutureNavigation()
    {
        var chrome = new PageChromeState();
        var oldOwner = new object();
        var currentOwner = new object();
        chrome.SetBack(oldOwner, () => Task.FromResult(true));
        chrome.SetBack(currentOwner, () => throw new InvalidOperationException());
        chrome.Remove(oldOwner);
        Assert.True(chrome.CanGoBack);
        await Assert.ThrowsAsync<InvalidOperationException>(chrome.TryGoBackAsync);
        chrome.SetBack(currentOwner, () => Task.FromResult(true));
        Assert.True(await chrome.TryGoBackAsync());
        chrome.Remove(currentOwner);
        Assert.False(chrome.CanGoBack);
        Assert.False(await chrome.TryGoBackAsync());
    }
    [Fact]
    public void ContentReadyNotifiesNativeLayoutForEveryNewPageRender()
    {
        var chrome = new PageChromeState();
        var generations = new List<long>();
        chrome.Changed += (_, _) => generations.Add(chrome.ContentGeneration);
        chrome.NotifyContentReady();
        chrome.NotifyContentReady();
        Assert.Equal(new long[] { 1, 2 }, generations);
        Assert.Equal(2, chrome.ContentGeneration);
    }
}
