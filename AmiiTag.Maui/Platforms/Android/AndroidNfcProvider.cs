using Android.Nfc;
using Android.Nfc.Tech;
using Android.OS;
using AmiiTag.Nfc;

namespace AmiiTag.Maui.NfcProviders;

/// <summary>
/// Android NFC using reader mode. Raw commands go through <see cref="NfcA"/>, which adds/checks CRCs itself.
/// </summary>
public sealed class AndroidNfcProvider : INfcProvider
{
    public string Id => "android-nfc";

    public string DisplayName => "Phone NFC";

    /// <summary>
    /// True when the phone has NFC hardware. If NFC is switched off the user is told when they try to use it.
    /// </summary>
    public bool IsSupported => GetAdapter() != null;

    public bool ShowsSystemPrompt => false;

    public bool SupportsReaderSelection => false;

    public Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NfcReaderInfo>>([new NfcReaderInfo(Id, DisplayName)]);

    private static NfcAdapter? GetAdapter() => NfcAdapter.GetDefaultAdapter(global::Android.App.Application.Context);

    public async Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default)
    {
        var adapter = GetAdapter() ?? throw new NfcException("This device doesn't have NFC.");
        if (!adapter.IsEnabled)
        {
            throw new NfcException("NFC is turned off. Turn it on in Settings.");
        }

        var activity = Platform.CurrentActivity ?? throw new NfcException("The app isn't in the foreground.");
        var callback = new ReaderCallback();

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var extras = new Bundle();
            extras.PutInt(NfcAdapter.ExtraReaderPresenceCheckDelay, 250);
            adapter.EnableReaderMode(activity, callback, NfcReaderFlags.NfcA | NfcReaderFlags.SkipNdefCheck, extras);
        });

        try
        {
            using var registration = cancellationToken.Register(() => callback.Discovered.TrySetCanceled(cancellationToken));
            var tag = await callback.Discovered.Task;

            var nfcA = NfcA.Get(tag) ?? throw new NfcException("This isn't an ISO 14443-A tag.");
            await Task.Run(() =>
            {
                nfcA.Connect();
                nfcA.Timeout = 800;
            }, cancellationToken);

            return new AndroidTagConnection(nfcA, tag.GetId() ?? Array.Empty<byte>(), () => DisableReaderMode(adapter, activity));
        }
        catch
        {
            await DisableReaderMode(adapter, activity);
            throw;
        }
    }

    private static Task DisableReaderMode(NfcAdapter adapter, global::Android.App.Activity activity) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                adapter.DisableReaderMode(activity);
            }
            catch
            {
            }
        });

    private sealed class ReaderCallback : Java.Lang.Object, NfcAdapter.IReaderCallback
    {
        public TaskCompletionSource<Tag> Discovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnTagDiscovered(Tag? tag)
        {
            if (tag != null)
            {
                Discovered.TrySetResult(tag);
            }
        }
    }
}

internal sealed class AndroidTagConnection : INfcTagConnection
{
    private readonly NfcA _nfcA;
    private readonly Func<Task> _onClose;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _closed;

    public AndroidTagConnection(NfcA nfcA, byte[] uid, Func<Task> onClose)
    {
        _nfcA = nfcA;
        _onClose = onClose;
        Uid = uid;
    }

    public byte[] Uid { get; }

    // NfcA.getMaxTransceiveLength is usually 253; 32 pages (128 bytes) is comfortably inside it.
    public int MaxFastReadPages => 32;

    public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default) =>
        ExchangeAsync(command, null, cancellationToken);

    public Task<byte[]> TransceiveAsync(byte[] command, TimeSpan responseTimeout, CancellationToken cancellationToken = default) =>
        ExchangeAsync(command, responseTimeout, cancellationToken);

    private async Task<byte[]> ExchangeAsync(byte[] command, TimeSpan? responseTimeout, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var previousTimeout = _nfcA.Timeout;
                try
                {
                    if (responseTimeout.HasValue)
                    {
                        _nfcA.Timeout = Math.Max(1, (int)responseTimeout.Value.TotalMilliseconds);
                    }
                    return _nfcA.Transceive(command) ?? Array.Empty<byte>();
                }
                catch (Java.IO.IOException ex)
                {
                    throw new NfcException($"Lost contact with the tag ({ex.Message}). Hold it still and try again.", ex);
                }
                catch (Java.Lang.Exception ex)
                {
                    throw new NfcException(ex.Message ?? "NFC error.", ex);
                }
                finally
                {
                    _nfcA.Timeout = previousTimeout;
                }
            }, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void SetStatus(string message)
    {
    }

    public async Task CloseAsync(string? errorMessage = null)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            _nfcA.Close();
        }
        catch
        {
        }

        await _onClose();
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
