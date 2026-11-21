using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmiiTag.AmiiboData;
using AmiiTag.Core;
using AmiiTag.Core.Data;
using AmiiTag.Core.Interfaces;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Microsoft.JSInterop;

namespace AmiiTag.WebAssembly.Data;

/// <summary>
/// The amiibo database on the web. Each source's sha1sum.json is the only thing fetched every time (its last good copy
/// is kept for when that fails); every file it lists (amiibo.json, games_info.json, the images) comes from an
/// IndexedDB cache keyed by its hash, and is downloaded only when that hash isn't cached yet. After an update, cached
/// files no source lists any more are removed. The loaded database is also saved in a compact binary form
/// (<see cref="AmiiboDatabaseSnapshot"/>) under a key made from the manifests; while they're unchanged it's loaded from
/// that, skipping the JSON parsing that makes a full import slow in the browser. Which databases, in what order, comes
/// from databases.json in the web app's own folder (<see cref="DatabaseSources"/>); the last copy fetched is kept in
/// local storage for when it can't be, and the built-in list is used when there's neither.
/// </summary>
public sealed class AmiiboDatabaseContainer(AmiiboDatabase database, IJSRuntime js, IAppPaths paths) : IDatabaseContainer
{
    private const string ListStorageKey = "amiitag.databases";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DatabaseSource> _sources = DatabaseSources.BuiltIn;
    private string[] UpdateUrls => _sources.Select(source => source.Url).ToArray();
    public AmiiboDatabase Database { get; } = database;
    public bool IsLoaded { get; private set; }
    public bool LoadsRemotely => true;
    public event EventHandler? DataChanged;
    public Task<bool> CheckUpdate(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public async Task ClearData(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await js.InvokeVoidAsync("amiitag.assets.clear", cancellationToken);
            Database.ClearData();
            IsLoaded = false;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    public Task LoadData(CancellationToken cancellationToken = default) => UpdateData(null, cancellationToken);

    public async Task UpdateData(Func<ProgressInfo, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _sources = await LoadSourcesAsync(cancellationToken);
            var manifests = new Dictionary<string, byte[]>();
            var cached = false;
            AmiiboDatabase staged;
            try { staged = await LoadOrImport(false, manifests, progress, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Re-import every source from its committed manifest and cached files, so a source whose hashes changed
                // can't leave the database half new and half old.
                cached = true;
                manifests.Clear();
                staged = await LoadOrImport(true, manifests, progress, cancellationToken);
            }

            if (!cached)
            {
                foreach (var item in manifests)
                    await js.InvokeVoidAsync("amiitag.assets.commitManifest", cancellationToken, item.Key, item.Value);
            }

            // The images the cache doesn't have yet. A missing image shows as a placeholder, not a failed database.
            var images = staged.Figures.Values.Select(f => f.ImageUri?.AbsoluteUri)
                .Where(url => url != null).Cast<string>().Distinct().ToArray();
            var uncached = cached ? [] : await js.InvokeAsync<string[]>("amiitag.assets.uncached", cancellationToken, (object)images);
            var completed = 0;
            foreach (var batch in uncached.Chunk(24))
            {
                await Report(progress, completed, uncached.Length, $"Downloading images ({completed} of {uncached.Length})");
                await js.InvokeAsync<int>("amiitag.assets.cacheFiles", cancellationToken, batch, false);
                completed += batch.Length;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Everything any source lists now; the rest of the cache is from versions no longer in use.
            await js.InvokeAsync<int>("amiitag.assets.retain", cancellationToken, (object)Hashes(manifests.Values).ToArray());

            Database.ReplaceData(staged);
            IsLoaded = Database.Figures.Count > 0;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Reads every source's manifest, then the database: from the saved snapshot when one was built from exactly these
    /// manifests, otherwise by importing the JSON (and saving a snapshot of the result).
    /// </summary>
    private async Task<AmiiboDatabase> LoadOrImport(bool cacheOnly, Dictionary<string, byte[]> manifests,
        Func<ProgressInfo, Task>? progress, CancellationToken ct)
    {
        var steps = UpdateUrls.Length + 1;
        for (var i = 0; i < UpdateUrls.Length; i++)
        {
            await Report(progress, i, steps, cacheOnly ? "Loading the cached database" : $"Checking {new Uri(UpdateUrls[i]).AbsolutePath.Trim('/')}");
            var url = ManifestUrl(UpdateUrls[i]);
            try
            {
                manifests[url] = await js.InvokeAsync<byte[]>("amiitag.assets.readManifest", ct, url, cacheOnly);
            }
            catch (JSException)
            {
                // No manifest at all (never loaded, and not reachable now): that source imports without hashes.
            }
        }

        var key = manifests.Count == UpdateUrls.Length ? SnapshotKey(manifests) : null;
        if (key != null)
        {
            var saved = await js.InvokeAsync<byte[]?>("amiitag.assets.readSnapshot", ct, key);
            if (saved != null && AmiiboDatabaseSnapshot.TryRead(saved) is { } snapshot)
            {
                await Report(progress, steps, steps, "Database loaded");
                return snapshot;
            }
        }

        await Report(progress, UpdateUrls.Length, steps, "Reading the amiibo database");
        var staged = await Import(cacheOnly, manifests, ct);
        if (key != null)
        {
            try
            {
                await js.InvokeVoidAsync("amiitag.assets.writeSnapshot", ct, key, AmiiboDatabaseSnapshot.Write(staged));
            }
            catch (JSException)
            {
                // Without a snapshot the next load just imports again (e.g. storage full).
            }
        }

        await Report(progress, steps, steps, "Database loaded");
        return staged;
    }

    private async Task<AmiiboDatabase> Import(bool cacheOnly, Dictionary<string, byte[]> manifests, CancellationToken ct)
    {
        var staged = new AmiiboDatabase();
        async Task<Stream> Download(Uri url, CancellationToken token)
        {
            byte[] bytes;
            if (url.AbsolutePath.EndsWith("/sha1sum.json", StringComparison.Ordinal))
            {
                // Already read for this load (it's fetched once).
                var key = new UriBuilder(url) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
                bytes = manifests.TryGetValue(key, out var manifest) ? manifest : throw new InvalidOperationException($"No manifest for {key}.");
            }
            else
            {
                bytes = await js.InvokeAsync<byte[]>("amiitag.assets.readFile", token, url.AbsoluteUri, cacheOnly);
            }

            return new MemoryStream(bytes, writable: false);
        }

        foreach (var source in UpdateUrls)
        {
            await staged.ImportData(source, cancellationToken: ct, download: Download);
        }

        return staged;
    }

    /// <summary>
    /// databases.json from the web app's own folder (bypassing the HTTP cache), kept in local storage; else the copy
    /// kept last time; else the built-in list.
    /// </summary>
    private async Task<IReadOnlyList<DatabaseSource>> LoadSourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(paths.BaseAddress) };
            using var request = new HttpRequestMessage(HttpMethod.Get, DatabaseSources.FileName);
            request.SetBrowserRequestCache(BrowserRequestCache.NoCache);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (DatabaseSources.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken)) is { } fetched)
            {
                try
                {
                    await js.InvokeVoidAsync("localStorage.setItem", cancellationToken, ListStorageKey,
                        Encoding.UTF8.GetString(DatabaseSources.Serialize(fetched)));
                }
                catch (JSException)
                {
                    // No local storage (private browsing, blocked): the list is just fetched every time.
                }

                return fetched;
            }

            Console.WriteLine($"AmiiTag: {DatabaseSources.FileName} isn't a usable list; using the last one kept.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"AmiiTag: couldn't fetch {DatabaseSources.FileName} ({ex.Message}); using the last one kept.");
        }

        try
        {
            if (await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, ListStorageKey) is { } kept &&
                DatabaseSources.Parse(Encoding.UTF8.GetBytes(kept)) is { } cached)
            {
                return cached;
            }
        }
        catch (JSException)
        {
        }

        return DatabaseSources.BuiltIn;
    }

    private static string ManifestUrl(string source) => new Uri(new Uri(source.TrimEnd('/') + "/"), "sha1sum.json").AbsoluteUri;

    /// <summary>
    /// Identifies the files a database was built from and the build that reads it: the snapshot layout and build
    /// (<see cref="AmiiboDatabaseSnapshot.SchemaId"/>), and every source's manifest (which lists the hash of each file,
    /// the images' included, as their URLs are part of the database).
    /// </summary>
    private static string SnapshotKey(Dictionary<string, byte[]> manifests)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{AmiiboDatabaseSnapshot.SchemaId}\n"));
        foreach (var (url, manifest) in manifests.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(url + "\n"));
            hash.AppendData(manifest);
            hash.AppendData("\n"u8);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// The hashes the manifests list (the files' SHA-1s), across all sources.
    /// </summary>
    private static HashSet<string> Hashes(IEnumerable<byte[]> manifests)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest);
            if (entries == null) continue;
            foreach (var hash in entries.Values) hashes.Add(hash.ToLowerInvariant());
        }

        return hashes;
    }

    private static Task Report(Func<ProgressInfo, Task>? progress, int current, int total, string message) =>
        progress == null ? Task.CompletedTask : progress(new ProgressInfo(current, total, message));
}
