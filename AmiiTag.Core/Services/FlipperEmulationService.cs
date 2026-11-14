using AmiiTag.Core.Interfaces;
using AmiiTag.Nfc.Flipper;
using AmiiTag.Simulation;
using AmiiTag.Tags;
using Microsoft.Extensions.DependencyInjection;

namespace AmiiTag.Core.Services;

/// <summary>
/// Tag emulation on a Flipper Zero running the NFC Bridge app (flipper/nfc_bridge). The Flipper relays a real
/// reader's frames over USB HID and AmiiTag answers them with its tag models, logging every exchange.
/// </summary>
/// <remarks>
/// What the reader writes goes back into the tag in <see cref="TagStore"/>, so its screen shows the new data and the
/// user can save it (or not).
/// </remarks>
public sealed class FlipperEmulationService : IDisposable
{
    private static readonly TimeSpan SyncDelay = TimeSpan.FromMilliseconds(400);

    private readonly IFlipperTransportProvider? _provider;
    private readonly IAppNavigator _navigator;
    private readonly IDialogService _dialogs;
    private readonly TagStore _tags;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _syncLock = new();
    private readonly HashSet<string> _unsaved = [];
    private Timer? _syncTimer;
    private IAmiiboImage? _original;
    private string? _tagKey;

    public FlipperEmulationService(IServiceProvider services, IAppNavigator navigator, IDialogService dialogs, TagStore tags)
    {
        _provider = services.GetService<IFlipperTransportProvider>();
        _navigator = navigator;
        _dialogs = dialogs;
        _tags = tags;
        if (_provider != null)
        {
            _provider.DevicesChanged += OnDevicesChanged;
        }
    }

    public bool IsSupported => _provider?.IsSupported == true;

    /// <summary>
    /// A Flipper running the bridge app is plugged in.
    /// </summary>
    public bool IsAvailable => IsSupported && _provider!.IsDevicePresent;

    public EmulationSession? Session { get; private set; }

    /// <summary>
    /// The tag (in <see cref="TagStore"/>) last emulated, and the library figure it was made from, if any: a tag
    /// screen reopened for that figure shows this tag rather than generating a new one.
    /// </summary>
    public string? LastTagKey { get; private set; }

    public string? LastFigureId { get; private set; }

    /// <summary>
    /// Availability or the session changed. May be raised off the UI thread.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// A reader wrote to the emulated tag; the tag with this key in <see cref="TagStore"/> now holds the new data.
    /// May be raised off the UI thread.
    /// </summary>
    public event Action<string>? TagUpdated;

    public static bool CanEmulate(IAmiiboImage image) => image is TagDump or AmiiboV3Dump;

    public static TagSimulator CreateSimulator(IAmiiboImage image) => image switch
    {
        TagDump dump => Ntag215Simulator.FromAmiiboDump(dump),
        AmiiboV3Dump v3 => NtagI2cPlusSimulator.FromImage(v3),
        _ => throw new NotSupportedException("This kind of tag can't be emulated."),
    };

    /// <summary>
    /// Whether a reader changed this tag during emulation and the user hasn't saved it since.
    /// </summary>
    public bool HasUnsavedChanges(string? tagKey)
    {
        lock (_syncLock)
        {
            return tagKey != null && _unsaved.Contains(tagKey);
        }
    }

    public void MarkSaved(string? tagKey)
    {
        lock (_syncLock)
        {
            if (tagKey != null)
            {
                _unsaved.Remove(tagKey);
            }
        }
    }

    /// <summary>
    /// Starts emulating the image (stopping any other emulation) and opens the emulation screen.
    /// </summary>
    /// <param name="tagKey">The image's key in <see cref="TagStore"/>, where the reader's writes go; added if null.</param>
    /// <param name="figureId">The library figure the image was generated from, if any.</param>
    public async Task EmulateAsync(IAmiiboImage image, string title, string? tagKey = null, string? figureId = null)
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (_provider == null || await _provider.OpenAsync() is not { } transport)
            {
                await _dialogs.AlertAsync("Flipper not found",
                    "Plug in the Flipper Zero and open Apps > USB > NFC Bridge on it.");
                return;
            }

            try
            {
                Session = await EmulationSession.StartAsync(transport, CreateSimulator(image), title);
            }
            catch (Exception ex)
            {
                await _dialogs.AlertAsync("Couldn't start emulating", ex.Message);
                return;
            }

            _original = image;
            _tagKey = tagKey ?? _tags.Add(image);
            LastTagKey = _tagKey;
            LastFigureId = figureId;
            Session.Changed += OnSessionChanged;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await _navigator.NavigateAsync(AppRoutes.Emulate());
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task StopCoreAsync()
    {
        if (Session is { } session)
        {
            Session = null;
            session.Changed -= OnSessionChanged;
            await session.DisposeAsync();

            // Nothing answers the reader any more: take what it wrote.
            SyncTag(session);
            _syncTimer?.Dispose();
            _syncTimer = null;
        }
    }

    /// <summary>
    /// Frames come in bursts; the tag is synced shortly after a burst (and when the session ends).
    /// </summary>
    private void OnSessionChanged()
    {
        var session = Session;
        if (session == null)
        {
            return;
        }

        lock (_syncLock)
        {
            _syncTimer ??= new Timer(_ => SyncTag(session), null, Timeout.Infinite, Timeout.Infinite);
            _syncTimer.Change(SyncDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Puts the emulated tag's memory into the stored image if a reader changed it.
    /// </summary>
    private void SyncTag(EmulationSession session)
    {
        string key;
        IAmiiboImage updated;
        lock (_syncLock)
        {
            if (_original == null || _tagKey == null || ApplyWrites(_original, session.Tag) is not { } image)
            {
                return;
            }

            if (_tags.Get(_tagKey) is { } current && SameBytes(current, image))
            {
                return;
            }

            key = _tagKey;
            updated = image;
            _tags.Replace(key, updated);
            _unsaved.Add(key);
        }

        session.Log.Add(EmulationLogKind.Info, "The reader changed the tag: its screen shows the new data (save it to keep it)");
        TagUpdated?.Invoke(key);
    }

    /// <summary>
    /// The original image with what a reader can write taken from the emulated tag: NTAG215 pages 0-132 (PWD, PACK
    /// and the signature stay as they were), or the v3 image's EEPROM (lock-on, session registers, PWD/PACK and
    /// invalid pages stay). Null when the image or model isn't one of those.
    /// </summary>
    public static IAmiiboImage? ApplyWrites(IAmiiboImage original, TagSimulator tag)
    {
        switch (original, tag)
        {
            case (TagDump dump, Ntag215Simulator ntag):
            {
                var data = dump.ToArray(dump.HasSignature ? TagDump.FullLength : TagDump.DataLength);
                ntag.Memory.AsSpan(0, Ntag215Simulator.PwdPage * 4).CopyTo(data);
                return new TagDump(data);
            }

            case (AmiiboV3Dump v3, NtagI2cPlusSimulator i2c):
            {
                var raw = v3.Span.ToArray();
                var sector0 = i2c.SectorMemory(0);
                for (var page = 0; page < NtagI2cPlusSimulator.SectorPages; page++)
                {
                    // Session registers aren't EEPROM; PWD/PACK read back as zeros; SRAM holds the lock-on.
                    var keep = page is NtagI2cPlusSimulator.PwdPage or NtagI2cPlusSimulator.PackPage
                        or NtagI2cPlusSimulator.SessionPage0 or NtagI2cPlusSimulator.SessionPage1
                        || page >= NtagI2cPlusSimulator.SramFirstPage
                        || NtagI2cPlusSimulator.IsInvalidPage(0, page);
                    if (!keep)
                    {
                        sector0.AsSpan(page * 4, 4).CopyTo(raw.AsSpan(page * 4));
                    }
                }

                i2c.SectorMemory(1).CopyTo(raw.AsSpan(0x400));
                return new AmiiboV3Dump(raw);
            }

            default:
                return null;
        }
    }

    private static bool SameBytes(IAmiiboImage a, IAmiiboImage b) => (a, b) switch
    {
        (TagDump x, TagDump y) => x.ToArray().AsSpan().SequenceEqual(y.ToArray()),
        (AmiiboV3Dump x, AmiiboV3Dump y) => x.Span.SequenceEqual(y.Span),
        _ => false,
    };

    private void OnDevicesChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_provider != null)
        {
            _provider.DevicesChanged -= OnDevicesChanged;
        }

        _ = StopAsync();
    }
}
