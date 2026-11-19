using System.Text;
using AmiiTag.AmiiboData;
using AmiiTag.Core.Interfaces;

namespace AmiiTag.Core.Services;

/// <summary>
/// An amiibo that works with a game, and what it does there.
/// </summary>
public sealed record GameAmiibo(AmiiboFigure Figure, IReadOnlyList<GameUsage> Usage)
{
    public bool SavesData => Usage.Any(u => u.Writable);
}

/// <summary>
/// A game from games_info.json, with every amiibo that works with it.
/// </summary>
public sealed class GameEntry
{
    internal GameEntry(string key, string name, SystemKind system)
    {
        Key = key;
        Name = name;
        System = system;
    }

    /// <summary>
    /// A stable, URL safe identifier.
    /// </summary>
    public string Key { get; }

    public string Name { get; }

    public SystemKind System { get; }

    public string SystemName => GameCatalog.SystemName(System);

    public IReadOnlyList<string> TitleIds => _titleIds.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<GameAmiibo> Amiibo => _amiibo;

    internal readonly HashSet<string> _titleIds = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<GameAmiibo> _amiibo = [];
}

/// <summary>
/// Indexes the per-amiibo game info from the database into a game → amiibo lookup.
/// Rebuilt automatically whenever the database reloads.
/// </summary>
public sealed class GameCatalog
{
    private readonly IDatabaseContainer _database;
    private readonly object _sync = new();
    private Index? _index;

    public GameCatalog(IDatabaseContainer database)
    {
        _database = database;
        _database.DataChanged += (_, _) =>
        {
            lock (_sync)
            {
                _index = null;
            }
        };
    }

    /// <summary>
    /// The systems, in the order they're shown.
    /// </summary>
    public static IReadOnlyList<SystemKind> Systems { get; } = [SystemKind._Switch, SystemKind._WiiU, SystemKind._3DS];

    public static string SystemName(SystemKind system) => system switch
    {
        SystemKind._Switch => "Switch",
        SystemKind._WiiU => "Wii U",
        SystemKind._3DS => "3DS",
        _ => "Other",
    };

    /// <summary>
    /// A short lowercase token used for CSS classes.
    /// </summary>
    public static string SystemToken(SystemKind system) => system switch
    {
        SystemKind._Switch => "switch",
        SystemKind._WiiU => "wiiu",
        SystemKind._3DS => "3ds",
        _ => "other",
    };

    /// <summary>
    /// Every game, sorted by name.
    /// </summary>
    public IReadOnlyList<GameEntry> Games => GetIndex().Games;

    public GameEntry? Get(string? key) => key != null && GetIndex().ByKey.TryGetValue(key, out var game) ? game : null;

    /// <summary>
    /// The games a figure works with and what it does in each, ordered by system then name.
    /// </summary>
    public IReadOnlyList<(GameEntry Game, IReadOnlyList<GameUsage> Usage)> ForFigure(AmiiboFigure figure)
    {
        var index = GetIndex();
        var result = new Dictionary<string, (GameEntry Game, List<GameUsage> Usage)>();
        foreach (var game in figure.Games)
        {
            if (!index.ByKey.TryGetValue(KeyFor(game.Name, game.System), out var entry))
            {
                continue;
            }

            if (!result.TryGetValue(entry.Key, out var existing))
            {
                existing = (entry, new List<GameUsage>());
                result[entry.Key] = existing;
            }

            AddUsages(existing.Usage, game.Usage);
        }

        return result.Values
            .OrderBy(r => SystemOrder(r.Game.System))
            .ThenBy(r => r.Game.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => (r.Game, (IReadOnlyList<GameUsage>)r.Usage))
            .ToList();
    }

    public static int SystemOrder(SystemKind system)
    {
        for (var i = 0; i < Systems.Count; i++)
        {
            if (Systems[i] == system)
            {
                return i;
            }
        }

        return Systems.Count;
    }

    private Index GetIndex()
    {
        lock (_sync)
        {
            return _index ??= Build(_database.Database);
        }
    }

    private static Index Build(AmiiboDatabase database)
    {
        var byKey = new Dictionary<string, GameEntry>(StringComparer.Ordinal);
        var usageByGameAndFigure = new Dictionary<(string Game, string Figure), List<GameUsage>>();

        foreach (var figure in database.Figures.Values)
        {
            foreach (var game in figure.Games)
            {
                if (string.IsNullOrWhiteSpace(game.Name))
                {
                    continue;
                }

                var key = KeyFor(game.Name, game.System);
                if (!byKey.TryGetValue(key, out var entry))
                {
                    entry = new GameEntry(key, game.Name.Trim(), game.System);
                    byKey[key] = entry;
                }

                entry._titleIds.UnionWith(game.Ids.Where(id => !string.IsNullOrWhiteSpace(id)));

                // A figure can be listed more than once for the same game; merge its usages.
                if (!usageByGameAndFigure.TryGetValue((key, figure.Id.Id), out var usage))
                {
                    usage = [];
                    usageByGameAndFigure[(key, figure.Id.Id)] = usage;
                    entry._amiibo.Add(new GameAmiibo(figure, usage));
                }

                AddUsages(usage, game.Usage);
            }
        }

        foreach (var entry in byKey.Values)
        {
            entry._amiibo.Sort((a, b) =>
            {
                var visible = b.Figure.IsVisible.CompareTo(a.Figure.IsVisible);
                return visible != 0 ? visible : StringComparer.OrdinalIgnoreCase.Compare(a.Figure.Name, b.Figure.Name);
            });
        }

        var games = byKey.Values
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => SystemOrder(g.System))
            .ToList();

        return new Index(games, byKey);
    }

    private static void AddUsages(List<GameUsage> target, IEnumerable<GameUsage> usages)
    {
        foreach (var usage in usages)
        {
            if (!string.IsNullOrWhiteSpace(usage.Description) &&
                !target.Any(u => u.Writable == usage.Writable && string.Equals(u.Description, usage.Description, StringComparison.Ordinal)))
            {
                target.Add(usage);
            }
        }
    }

    /// <summary>
    /// Stable across reloads and sessions (FNV-1a of system + name), so web URLs keep working.
    /// </summary>
    public static string KeyFor(string name, SystemKind system)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes($"{SystemToken(system)}|{name.Trim().ToLowerInvariant()}"))
        {
            hash ^= b;
            hash *= prime;
        }

        return $"{SystemToken(system)}-{hash:x16}";
    }

    private sealed record Index(IReadOnlyList<GameEntry> Games, IReadOnlyDictionary<string, GameEntry> ByKey);
}
