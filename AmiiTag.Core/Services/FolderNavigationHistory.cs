namespace AmiiTag.Core.Services;

/// <summary>Committed folder visits, including the current folder. Up never pushes the folder being left.</summary>
public sealed class FolderNavigationHistory
{
    private readonly List<string> _paths = [];
    public bool CanGoBack => _paths.Count > 1;
    public string? Previous => CanGoBack ? _paths[^2] : null;

    public void Visit(string path)
    {
        if (_paths.Count == 0 || _paths[^1] != path) _paths.Add(path);
    }

    public void Back()
    {
        if (CanGoBack) _paths.RemoveAt(_paths.Count - 1);
    }

    public void UpTo(string path)
    {
        var previous = _paths.LastIndexOf(path);
        if (previous >= 0) _paths.RemoveRange(previous + 1, _paths.Count - previous - 1);
        else
        {
            if (_paths.Count > 0) _paths.RemoveAt(_paths.Count - 1);
            Visit(path);
        }
    }

    public void Clear() => _paths.Clear();
}
