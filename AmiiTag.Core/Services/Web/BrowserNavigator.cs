using AmiiTag.Core.Interfaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace AmiiTag.Core.Services.Web;

/// <summary>App routes append browser entries; Back/PopTo traverse committed entries.</summary>
public sealed class BrowserNavigator : IAppNavigator, IDisposable
{
    private sealed record Entry(string Uri, string? State);
    private readonly NavigationManager _navigation;
    private readonly IJSRuntime _js;
    private readonly List<Entry> _entries;
    private readonly HashSet<string> _newEntries = [];
    private int _position;
    private TaskCompletionSource? _movement;
    private Entry? _moveTarget;

    public BrowserNavigator(NavigationManager navigation, IJSRuntime js)
    {
        _navigation = navigation;
        _js = js;
        _entries = [new(navigation.Uri, navigation.HistoryEntryState)];
        _navigation.LocationChanged += OnLocationChanged;
    }

    // LocationChangingContext doesn't distinguish browser history from .NET
    // navigation. Give new app entries a one-use public HistoryEntryState token
    // instead of guessing from popstate events (including Blazor's own rollback).
    public bool IsAppNavigation(LocationChangingContext context) =>
        context.HistoryEntryState is { } state && _newEntries.Contains(state) ||
        _moveTarget is { } target && target.Uri == context.TargetLocation && target.State == context.HistoryEntryState;

    public async Task NavigateAsync(AppRoute route)
    {
        if (_movement is { } movement) await movement.Task;
        Navigate(route.WebPath);
    }

    private void Navigate(string path, bool replace = false)
    {
        var state = "amiitag:" + Guid.NewGuid().ToString("N");
        _newEntries.Add(state);
        _navigation.NavigateTo(path, new NavigationOptions { HistoryEntryState = state, ReplaceHistoryEntry = replace });
    }

    public async Task GoBackAsync()
    {
        if (_movement is { } pending) { await pending.Task; return; }
        if (_position > 0) await MoveAsync(_position - 1);
        else if (_navigation.Uri != _navigation.ToAbsoluteUri("").AbsoluteUri)
            Navigate("", replace: true); // Direct URL: no prior app entry to traverse.
    }

    public Task GoHomeAsync() => PopToAsync(AppRoutes.Home());

    public async Task PopToAsync(AppRoute route)
    {
        if (_movement is { } pending) await pending.Task;
        var uri = _navigation.ToAbsoluteUri(route.WebPath).AbsoluteUri;
        var target = _entries.FindLastIndex(_position, e => e.Uri == uri);
        if (target >= 0)
        {
            if (target != _position) await MoveAsync(target);
        }
        else Navigate(route.WebPath); // An unopened destination is a new entry.
    }

    private async Task MoveAsync(int target)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _movement = completion;
        _moveTarget = _entries[target];
        try
        {
            await _js.InvokeVoidAsync("amiitag.goHistory", target - _position);
            await completion.Task;
        }
        finally
        {
            if (ReferenceEquals(_movement, completion)) { _movement = null; _moveTarget = null; }
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var state = _navigation.HistoryEntryState;
        var isNew = state != null && _newEntries.Remove(state);
        var existing = isNew ? -1 : _entries.FindIndex(entry => entry.Uri == e.Location && entry.State == state);
        if (existing >= 0) _position = existing;
        else
        {
            if (_position + 1 < _entries.Count) _entries.RemoveRange(_position + 1, _entries.Count - _position - 1);
            _entries.Add(new Entry(e.Location, state));
            _position = _entries.Count - 1;
        }
        var movement = _movement;
        _movement = null;
        _moveTarget = null;
        movement?.TrySetResult();
    }

    public void Dispose()
    {
        _navigation.LocationChanged -= OnLocationChanged;
        _movement?.TrySetCanceled();
    }
}
