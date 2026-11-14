using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmiiTag.Core.Interfaces;
using AmiiTag.Tags;

namespace AmiiTag.Core.Services;

/// <summary>
/// A named lock-on response kept in the <see cref="LockOnLibrary"/>.
/// </summary>
public sealed class LockOnEntry
{
    /// <summary>
    /// <see cref="LockOnLibrary.IdFor"/> of the response: the same lock-on has the same ID (and file name) everywhere.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The user's name for it ("Zelda (my figure)").
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The 16 hex digit amiibo ID of the figure it came from, when known.
    /// </summary>
    public string? AmiiboId { get; set; }

    /// <summary>
    /// The UID of the figure it came from, when known.
    /// </summary>
    public string? SourceUid { get; set; }

    public DateTimeOffset Added { get; set; }

    /// <summary>
    /// The 64 byte response.
    /// </summary>
    public byte[] Response { get; set; } = [];

    /// <summary>
    /// Where it was loaded from, if that isn't lockons/{Id}.json (a file renamed or copied by hand).
    /// </summary>
    [JsonIgnore]
    internal string? StorageName { get; set; }

    [JsonIgnore]
    public LockOnCheck Check => LockOnResponse.Check(Response);
}

/// <summary>
/// The user's lock-on responses (from figures they've read, or .lockon files), each under a name of their choosing, so
/// they can be put back into v3 images later. One JSON file per entry in <see cref="IAppStorage"/>, named by a hash of
/// the response (lockons/{id}.json), so copying libraries between devices merges them without duplicates.
/// </summary>
public sealed class LockOnLibrary
{
    private const string Prefix = "lockons/";

    private readonly IAppStorage _storage;
    private readonly Dictionary<string, LockOnEntry> _entries = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public LockOnLibrary(IAppStorage storage)
    {
        _storage = storage;
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Newest first.
    /// </summary>
    public IReadOnlyList<LockOnEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.Values.OrderByDescending(e => e.Added).ToList();
            }
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            if (_loaded)
            {
                return;
            }

            foreach (var name in await _storage.ListAsync(Prefix))
            {
                try
                {
                    var data = await _storage.ReadAsync(name);
                    var entry = data == null ? null : JsonSerializer.Deserialize<LockOnEntry>(data);
                    if (entry is { Response.Length: LockOnResponse.Length })
                    {
                        // The ID always follows the data, whatever the file says.
                        entry.Id = IdFor(entry.Response);
                        entry.StorageName = name == StorageNameFor(entry.Id) ? null : name;
                        lock (_entries)
                        {
                            _entries[entry.Id] = entry;
                        }
                    }
                }
                catch (JsonException)
                {
                    // A damaged entry is skipped rather than losing the rest.
                }
            }

            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A lock-on's ID: the first 16 hex digits (64 bits) of the SHA-256 of its 64 bytes.
    /// </summary>
    public static string IdFor(ReadOnlySpan<byte> response) => Hex.ToHex(SHA256.HashData(response).AsSpan(0, 8)).ToLowerInvariant();

    /// <summary>
    /// The entry holding exactly this response, if any.
    /// </summary>
    public LockOnEntry? Find(ReadOnlySpan<byte> response)
    {
        if (response.Length != LockOnResponse.Length)
        {
            return null;
        }

        lock (_entries)
        {
            return _entries.TryGetValue(IdFor(response), out var entry) && response.SequenceEqual(entry.Response) ? entry : null;
        }
    }

    /// <summary>
    /// Stores a response (1-62 byte payloads get a CRC; 64 bytes are kept as they are) under a name. Returns the
    /// existing entry instead if the same response is already stored.
    /// </summary>
    public async Task<(LockOnEntry Entry, bool Added)> AddAsync(string name, ReadOnlyMemory<byte> response, string? amiiboId = null, string? sourceUid = null)
    {
        await EnsureLoadedAsync();
        var normalized = LockOnResponse.Normalize(response.Span);
        var check = LockOnResponse.Check(normalized);
        if (!LockOnResponse.IsUsable(check))
        {
            throw new TagException($"This isn't a figure's lock-on response: {LockOnResponse.Describe(check)}.");
        }

        if (Find(normalized) is { } existing)
        {
            return (existing, false);
        }

        var entry = new LockOnEntry
        {
            Id = IdFor(normalized),
            Name = CleanName(name),
            AmiiboId = amiiboId?.ToLowerInvariant(),
            SourceUid = sourceUid,
            Added = DateTimeOffset.Now,
            Response = normalized,
        };
        await SaveAsync(entry);
        lock (_entries)
        {
            _entries[entry.Id] = entry;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return (entry, true);
    }

    public async Task RenameAsync(LockOnEntry entry, string name)
    {
        entry.Name = CleanName(name);
        await SaveAsync(entry);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(LockOnEntry entry)
    {
        await _storage.DeleteAsync(entry.StorageName ?? StorageNameFor(entry.Id));
        lock (_entries)
        {
            _entries.Remove(entry.Id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task SaveAsync(LockOnEntry entry) =>
        _storage.WriteAsync(entry.StorageName ?? StorageNameFor(entry.Id), JsonSerializer.SerializeToUtf8Bytes(entry));

    private static string StorageNameFor(string id) => $"{Prefix}{id}.json";

    private static string CleanName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "Lock-on" : trimmed.Length > 60 ? trimmed[..60] : trimmed;
    }
}
