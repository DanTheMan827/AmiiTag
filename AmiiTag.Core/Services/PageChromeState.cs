namespace AmiiTag.Core.Services;

/// <summary>One native page's toolbar and refresh callbacks. Never shared between WebViews.</summary>
public sealed class PageChromeState(bool supportsPullToRefresh = false)
{
    private object? _settingsOwner;
    private object? _refreshOwner;
    private Func<Task>? _settings;
    private Func<Task>? _refresh;
    private int _refreshing;
    private object? _backOwner;
    private Func<Task<bool>>? _back;
    private int _goingBack;
    public bool CanGoBack => _back != null;

    public void SetBack(object owner, Func<Task<bool>>? action)
    {
        _backOwner = owner;
        _back = action;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> TryGoBackAsync()
    {
        var back = _back;
        if (back == null) return false;
        if (Interlocked.CompareExchange(ref _goingBack, 1, 0) != 0) return true;
        try { return await back(); }
        finally { Interlocked.Exchange(ref _goingBack, 0); }
    }

    public bool SupportsPullToRefresh { get; } = supportsPullToRefresh;
    public bool IsRefreshing => Volatile.Read(ref _refreshing) != 0;
    public bool HasSettings => _settings != null;
    public bool CanRefresh => SupportsPullToRefresh && !_pullBlocked && _refresh != null;
    private bool _pullBlocked;

    public void SetPullBlocked(bool blocked)
    {
        if (_pullBlocked == blocked) return;
        _pullBlocked = blocked;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public event EventHandler? Changed;
    private long _contentGeneration;
    public long ContentGeneration => Interlocked.Read(ref _contentGeneration);
    public void NotifyContentReady()
    {
        Interlocked.Increment(ref _contentGeneration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetSettings(object owner, Func<Task>? action)
    {
        _settingsOwner = owner;
        _settings = action;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetRefresh(object owner, Func<Task>? action)
    {
        _refreshOwner = owner;
        _refresh = action;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(object owner)
    {
        if (ReferenceEquals(_backOwner, owner)) { _back = null; _backOwner = null; }
        if (ReferenceEquals(_settingsOwner, owner)) { _settings = null; _settingsOwner = null; }
        if (ReferenceEquals(_refreshOwner, owner)) { _refresh = null; _refreshOwner = null; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task OpenSettingsAsync() => _settings?.Invoke() ?? Task.CompletedTask;

    public async Task RefreshAsync()
    {
        var refresh = _refresh;
        if (!CanRefresh || refresh == null || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        try { await refresh(); }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }
}
