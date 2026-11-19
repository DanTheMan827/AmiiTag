using AmiiTag.Core.Services;
using AmiiTag.Core.Services.Web;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AmiiTag.Core.Tests;

public sealed class BrowserNavigatorTests : BunitContext
{
    private BunitNavigationManager Navigation => (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
    private sealed record Entry(string Uri, string? State);
    private Entry Current => new(Navigation.Uri, Navigation.HistoryEntryState);
    private void Arrive(Entry entry) => Navigation.NavigateTo(entry.Uri,
        new NavigationOptions { HistoryEntryState = entry.State });

    [Fact]
    public async Task DeviceAndAutomaticTagAppendDistinctEntries()
    {
        using var navigator = new BrowserNavigator(Navigation, JSInterop.JSRuntime);
        await navigator.NavigateAsync(AppRoutes.Device("pixl", "Pixl"));
        var device = Current;
        await navigator.NavigateAsync(AppRoutes.Tag("test", "Test", true, automatic: true));
        var tag = Current;
        Assert.NotEqual(device.State, tag.State);
        Assert.EndsWith("/tag/test?auto=1", tag.Uri);
        Assert.Equal(2, Navigation.History.Count);
        Assert.All(Navigation.History, h => Assert.False(h.Options.ReplaceHistoryEntry));
    }

    [Fact]
    public async Task NewAppIntentDoesNotDependOnPopstate_AndCannotBeReplayedByHistory()
    {
        using var navigator = new BrowserNavigator(Navigation, JSInterop.JSRuntime);
        var intents = new List<bool>();
        using var guard = Navigation.RegisterLocationChangingHandler(c =>
        {
            intents.Add(navigator.IsAppNavigation(c));
            return ValueTask.CompletedTask;
        });
        await navigator.NavigateAsync(AppRoutes.Device("pixl", "Pixl"));
        var device = Current;
        await navigator.NavigateAsync(AppRoutes.Settings());
        Arrive(device); // Browser Back: state belongs to an already committed entry.
        Assert.Equal(new[] { true, true, false }, intents);
    }

    [Fact]
    public async Task ScannedTagBackTraversesToHome_WithoutReplacingTheTagEntry()
    {
        using var navigator = new BrowserNavigator(Navigation, JSInterop.JSRuntime);
        var home = Current;
        await navigator.NavigateAsync(AppRoutes.Tag("scan", "Scanned tag", true, automatic: true));
        JSInterop.SetupVoid("amiitag.goHistory", -1).SetVoidResult();
        var back = navigator.GoBackAsync();
        Assert.False(back.IsCompleted);
        Assert.Single(Navigation.History); // No new or replacement route from Back.
        Arrive(home);
        await back;
        Assert.Equal(home.Uri, Navigation.Uri);
        Assert.All(Navigation.History, h => Assert.False(h.Options.ReplaceHistoryEntry));
    }

    [Fact]
    public async Task PopToTraversesMultipleEntries_InsteadOfReplacingCurrentEntry()
    {
        using var navigator = new BrowserNavigator(Navigation, JSInterop.JSRuntime);
        var home = Current;
        await navigator.NavigateAsync(AppRoutes.Library());
        await navigator.NavigateAsync(AppRoutes.Series("mario", "Mario"));
        await navigator.NavigateAsync(AppRoutes.Figure("mario", "Mario"));
        JSInterop.SetupVoid("amiitag.goHistory", -3).SetVoidResult();
        var back = navigator.PopToAsync(AppRoutes.Home());
        Assert.Equal(3, Navigation.History.Count);
        Arrive(home);
        await back;
        Assert.All(Navigation.History, h => Assert.False(h.Options.ReplaceHistoryEntry));
    }

    [Fact]
    public async Task RepeatedBackIsCoalesced_AndForwardNavigationWaitsForBackToCommit()
    {
        using var navigator = new BrowserNavigator(Navigation, JSInterop.JSRuntime);
        await navigator.NavigateAsync(AppRoutes.Library());
        var library = Current;
        await navigator.NavigateAsync(AppRoutes.Tag("scan", "Scan", true, automatic: true));
        JSInterop.SetupVoid("amiitag.goHistory", -1).SetVoidResult();
        var first = navigator.GoBackAsync();
        var repeated = navigator.GoBackAsync();
        var next = navigator.NavigateAsync(AppRoutes.Settings());
        Assert.False(next.IsCompleted);
        Assert.Single(JSInterop.Invocations);
        Arrive(library);
        await Task.WhenAll(first, repeated, next);
        Assert.EndsWith("/settings", Navigation.Uri);
        Assert.False(Navigation.History.First().Options.ReplaceHistoryEntry);
    }

    [Fact]
    public async Task NewPageAfterBackDiscardsForwardBranchForLaterPopTo()
    {
        using var navigator = new BrowserNavigator(Navigation, JSInterop.JSRuntime);
        await navigator.NavigateAsync(AppRoutes.Library());
        var library = Current;
        await navigator.NavigateAsync(AppRoutes.Series("mario", "Mario"));
        JSInterop.SetupVoid("amiitag.goHistory", -1).SetVoidResult();
        var back = navigator.GoBackAsync(); Arrive(library); await back;
        await navigator.NavigateAsync(AppRoutes.Settings());
        JSInterop.SetupVoid("amiitag.goHistory", -2).SetVoidResult();
        var home = new Entry(Navigation.BaseUri, null);
        var toHome = navigator.GoHomeAsync(); Arrive(home); await toHome;
        Assert.Equal(home.Uri, Navigation.Uri);
    }

    [Fact]
    public async Task UnopenedPopToDestinationAppends_AndDirectLinkBackUsesHomeFallback()
    {
        Navigation.NavigateTo("/device/direct");
        using var navigator = new BrowserNavigator(Navigation, JSInterop.JSRuntime);
        await navigator.GoBackAsync();
        Assert.True(Navigation.History.First().Options.ReplaceHistoryEntry);
        await navigator.PopToAsync(AppRoutes.Settings());
        Assert.False(Navigation.History.First().Options.ReplaceHistoryEntry);
        Assert.EndsWith("/settings", Navigation.Uri);
    }
}
