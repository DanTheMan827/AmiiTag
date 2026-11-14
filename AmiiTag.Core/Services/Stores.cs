using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AmiiTag.Core.Interfaces;
using AmiiTag.Tags;

namespace AmiiTag.Core.Services;

/// <summary>
/// The outcome of loading key files.
/// </summary>
/// <param name="HasBothKeys">Encryption is possible.</param>
/// <param name="HasUnfixedInfo">The data key is loaded (decrypting works).</param>
/// <param name="HasLockedSecret">The static key is loaded.</param>
/// <param name="Used">Files that were recognised, with what they are.</param>
/// <param name="Ignored">Files that weren't key files.</param>
public sealed record KeyImportResult(
    bool HasBothKeys,
    bool HasUnfixedInfo,
    bool HasLockedSecret,
    IReadOnlyList<string> Used,
    IReadOnlyList<string> Ignored);

/// <summary>
/// Holds the amiibo keys: key_retail.bin, or the split unfixed-info.bin and locked-secret.bin. Files are recognised by
/// size and SHA-256. Both keys are needed to encrypt; unfixed-info.bin alone is enough to decrypt.
/// Port of the Swift <c>KeyFiles</c>.
/// </summary>
public sealed class KeyStore
{
    public const string StorageName = "key_retail.bin";
    public const string UnfixedInfoStorageName = "unfixed-info.bin";
    public const string LockedSecretStorageName = "locked-secret.bin";

    private readonly IAppStorage _storage;
    private bool _loaded;
    private byte[]? _unfixedInfo;
    private byte[]? _lockedSecret;

    public KeyStore(IAppStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Both keys, or null. Everything that encrypts uses this.
    /// </summary>
    public AmiiboKeySet? Keys { get; private set; }

    /// <summary>
    /// Keys that can at least decrypt (both keys, or unfixed-info.bin alone), for showing nicknames and app data.
    /// </summary>
    public AmiiboKeySet? DecryptionKeys => Keys ?? (_unfixedInfo != null ? AmiiboKeySet.FromUnfixedInfo(_unfixedInfo) : null);

    public bool HasKeys => Keys != null;

    public bool HasUnfixedInfo => Keys != null || _unfixedInfo != null;

    public bool HasLockedSecret => Keys != null || _lockedSecret != null;

    public bool HasAnyKey => HasUnfixedInfo || HasLockedSecret;

    public event EventHandler? Changed;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            var combined = await _storage.ReadAsync(StorageName);
            if (combined != null && AmiiboKeyFiles.Identify(combined) == AmiiboKeyFileKind.Combined)
            {
                Keys = AmiiboKeySet.FromKeyFile(combined);
                return;
            }

            var unfixed = await _storage.ReadAsync(UnfixedInfoStorageName);
            var locked = await _storage.ReadAsync(LockedSecretStorageName);
            _unfixedInfo = unfixed != null && AmiiboKeyFiles.Identify(unfixed) == AmiiboKeyFileKind.UnfixedInfo ? unfixed : null;
            _lockedSecret = locked != null && AmiiboKeyFiles.Identify(locked) == AmiiboKeyFileKind.LockedSecret ? locked : null;
            if (_unfixedInfo != null && _lockedSecret != null)
            {
                Keys = AmiiboKeySet.FromParts(_unfixedInfo, _lockedSecret);
            }
        }
        catch
        {
            Keys = null;
        }
    }

    /// <summary>
    /// Validates and stores a single key file. Throws <see cref="TagException"/> if it isn't one.
    /// </summary>
    public Task<KeyImportResult> ImportAsync(byte[] data) => ImportAsync([new PickedFile(StorageName, data)]);

    /// <summary>
    /// Loads any mix of key_retail.bin, unfixed-info.bin and locked-secret.bin (in any order, with any names), keeping
    /// the ones recognised by size and SHA-256 and ignoring the rest. A half loaded earlier is combined with its other
    /// half when that arrives. Throws <see cref="TagException"/> if none of the files is a key file.
    /// </summary>
    public async Task<KeyImportResult> ImportAsync(IReadOnlyList<PickedFile> files)
    {
        await EnsureLoadedAsync();

        byte[]? combined = null;
        byte[]? unfixed = null;
        byte[]? locked = null;
        var used = new List<string>();
        var ignored = new List<string>();

        foreach (var file in files)
        {
            var kind = AmiiboKeyFiles.Identify(file.Data);
            switch (kind)
            {
                case AmiiboKeyFileKind.Combined:
                    combined = file.Data;
                    break;
                case AmiiboKeyFileKind.UnfixedInfo:
                    unfixed = file.Data;
                    break;
                case AmiiboKeyFileKind.LockedSecret:
                    locked = file.Data;
                    break;
                default:
                    ignored.Add(file.Name);
                    continue;
            }

            used.Add($"{file.Name} ({AmiiboKeyFiles.Describe(kind)})");
        }

        if (used.Count == 0)
        {
            throw new TagException(
                $"None of the selected files is an amiibo key file. Expected key_retail.bin ({AmiiboKeyFiles.CombinedLength} bytes) " +
                $"or unfixed-info.bin and locked-secret.bin ({AmiiboKeyFiles.HalfLength} bytes each).");
        }

        if (combined == null && unfixed != null | _unfixedInfo != null && locked != null | _lockedSecret != null)
        {
            var pairUnfixed = unfixed ?? _unfixedInfo!;
            var pairLocked = locked ?? _lockedSecret!;
            combined = [.. pairUnfixed, .. pairLocked];
        }

        if (combined != null)
        {
            Keys = AmiiboKeySet.FromKeyFile(combined);
            await _storage.WriteAsync(StorageName, combined);
            await _storage.DeleteAsync(UnfixedInfoStorageName);
            await _storage.DeleteAsync(LockedSecretStorageName);
            _unfixedInfo = null;
            _lockedSecret = null;
        }
        else
        {
            if (unfixed != null)
            {
                _unfixedInfo = unfixed;
                await _storage.WriteAsync(UnfixedInfoStorageName, unfixed);
            }

            if (locked != null)
            {
                _lockedSecret = locked;
                await _storage.WriteAsync(LockedSecretStorageName, locked);
            }
        }

        _loaded = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return new KeyImportResult(HasKeys, HasUnfixedInfo, HasLockedSecret, used, ignored);
    }

    public async Task RemoveAsync()
    {
        await _storage.DeleteAsync(StorageName);
        await _storage.DeleteAsync(UnfixedInfoStorageName);
        await _storage.DeleteAsync(LockedSecretStorageName);
        Keys = null;
        _unfixedInfo = null;
        _lockedSecret = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The raw key file (for uploading to pucks), if both keys are loaded.
    /// </summary>
    public byte[]? RawKeys => Keys is { CanEncrypt: true } keys ? keys.Raw : null;
}

/// <summary>
/// Saves UID + originality signature pairs from scanned genuine tags, so generated tags can use real ones.
/// Port of the Swift Signatures folder (42 byte files: 9 byte UID, 1 byte, 32 byte signature).
/// </summary>
public sealed class SignatureStore
{
    private const string Prefix = "signatures/";

    private readonly IAppStorage _storage;
    private readonly ConcurrentDictionary<string, byte[]> _records = new();
    private bool _loaded;

    public SignatureStore(IAppStorage storage)
    {
        _storage = storage;
    }

    public int Count => _records.Count;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        foreach (var name in await _storage.ListAsync(Prefix))
        {
            var data = await _storage.ReadAsync(name);
            if (data is { Length: 42 } && NtagOriginality.Verify(data.AsSpan(0, 9), data.AsSpan(10, 32)))
            {
                _records[Hex.ToHex(data.AsSpan(0, 9))] = data;
            }
        }
    }

    /// <summary>
    /// Remembers the tag's signature if it's genuine. Returns true if it was new.
    /// </summary>
    public async Task<bool> AddAsync(TagDump dump)
    {
        await EnsureLoadedAsync();
        var record = dump.UidSignatureRecord;
        if (record == null || !dump.VerifyOriginality())
        {
            return false;
        }

        var key = Hex.ToHex(dump.Uid);
        if (!_records.TryAdd(key, record))
        {
            return false;
        }

        await _storage.WriteAsync($"{Prefix}{key}.bin", record);
        return true;
    }

    /// <summary>
    /// Picks a random stored UID/signature pair, or a random UID with an empty signature if none are stored.
    /// </summary>
    public async Task<(byte[] Uid, byte[] Signature)> GetRandomAsync()
    {
        await EnsureLoadedAsync();
        var records = _records.Values.ToArray();
        if (records.Length == 0)
        {
            return (NtagUid.Random(), new byte[TagDump.SignatureLength]);
        }

        var record = records[RandomNumberGenerator.GetInt32(records.Length)];
        return (record.AsSpan(0, 9).ToArray(), record.AsSpan(10, 32).ToArray());
    }
}

public sealed class AppSettings
{
    public string? NfcProviderId { get; set; }
    public string? NfcReaderId { get; set; }

    /// <summary>
    /// Randomize the UID (using a stored genuine signature if available) when creating tags from the library.
    /// </summary>
    public bool RandomizeGeneratedUid { get; set; } = true;

    /// <summary>
    /// UART speed for an MFRC522 serial reader (it's switched to this from its power-on 9600).
    /// </summary>
    public int Mfrc522BaudRate { get; set; } = 921600;

    /// <summary>
    /// Set once Bluetooth has been used (permission granted), after which tag screens scan in the background so nearby
    /// devices can be offered when writing, without a surprise permission prompt.
    /// </summary>
    public bool BluetoothUsed { get; set; }
}

public sealed class SettingsStore
{
    private const string StorageName = "settings.json";

    private readonly IAppStorage _storage;
    private bool _loaded;

    public SettingsStore(IAppStorage storage)
    {
        _storage = storage;
    }

    public AppSettings Current { get; private set; } = new();

    public async Task<AppSettings> GetAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                var data = await _storage.ReadAsync(StorageName);
                if (data != null)
                {
                    Current = JsonSerializer.Deserialize<AppSettings>(data) ?? new AppSettings();
                }
            }
            catch
            {
                Current = new AppSettings();
            }
        }

        return Current;
    }

    public async Task SaveAsync()
    {
        await _storage.WriteAsync(StorageName, JsonSerializer.SerializeToUtf8Bytes(Current));
    }
}

/// <summary>
/// Keeps tags in memory so screens can pass them around by key (URLs on the web, parameters on MAUI).
/// </summary>
public sealed class TagStore
{
    private readonly ConcurrentDictionary<string, IAmiiboImage> _tags = new();
    private readonly ConcurrentDictionary<string, string> _figureKeys = new();

    /// <summary>
    /// A tag was replaced (changed on another screen, such as the lock-on picker); its key.
    /// </summary>
    public event Action<string>? Replaced;

    public string Add(IAmiiboImage dump)
    {
        var key = Guid.NewGuid().ToString("N")[..12];
        _tags[key] = dump;
        return key;
    }

    public void Replace(string key, IAmiiboImage dump)
    {
        _tags[key] = dump;
        Replaced?.Invoke(key);
    }

    /// <summary>
    /// The tag a library figure's page generated and then changed (a lock-on, a new UID...), so going back to that
    /// figure (the web rebuilds the page) shows it rather than a newly generated one.
    /// </summary>
    public void RememberForFigure(string figureId, string key) => _figureKeys[figureId] = key;

    public string? KeyForFigure(string figureId) =>
        _figureKeys.TryGetValue(figureId, out var key) && _tags.ContainsKey(key) ? key : null;

    public IAmiiboImage? Get(string? key) => key != null && _tags.TryGetValue(key, out var dump) ? dump : null;
}
