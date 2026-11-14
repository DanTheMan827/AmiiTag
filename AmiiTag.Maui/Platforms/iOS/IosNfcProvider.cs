using AmiiTag.Nfc;
using CoreNFC;
using Foundation;
using ObjCRuntime;

namespace AmiiTag.Maui.NfcProviders;

/// <summary>
/// CoreNFC tag reading. Port of the Swift <c>NFCTagReader</c>; raw commands use <c>sendMiFareCommand</c>.
/// </summary>
public sealed class IosNfcProvider : INfcProvider
{
    // NFCReaderError codes.
    private const long UserCanceled = 200;
    private const long SessionTimeout = 201;

    public string Id => "ios-corenfc";

    public string DisplayName => "iPhone NFC";

    public bool IsSupported => NFCTagReaderSession.ReadingAvailable;

    public bool ShowsSystemPrompt => true;

    public bool SupportsReaderSelection => false;

    public Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NfcReaderInfo>>([new NfcReaderInfo(Id, DisplayName)]);

    public async Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default)
    {
        var sessionDelegate = new SessionDelegate();

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var session = new NFCTagReaderSession(NFCPollingOption.Iso14443, sessionDelegate, null);
            sessionDelegate.Session = session;
            session.AlertMessage = options.Prompt;
            session.BeginSession();
        });

        using var registration = cancellationToken.Register(() =>
        {
            MainThread.BeginInvokeOnMainThread(() => sessionDelegate.Session?.InvalidateSession());
            sessionDelegate.Connected.TrySetCanceled(cancellationToken);
        });

        return await sessionDelegate.Connected.Task;
    }

    private sealed class SessionDelegate : NFCTagReaderSessionDelegate
    {
        public NFCTagReaderSession? Session { get; set; }

        public TaskCompletionSource<INfcTagConnection> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IosTagConnection? Connection { get; private set; }

        public override void DidBecomeActive(NFCTagReaderSession session)
        {
        }

        public override void DidInvalidate(NFCTagReaderSession session, NSError error)
        {
            Connection?.MarkInvalidated();

            if (error.Code == UserCanceled || error.Code == SessionTimeout)
            {
                Connected.TrySetCanceled();
            }
            else
            {
                Connected.TrySetException(new NfcException(error.LocalizedDescription));
            }
        }

        public override void DidDetectTags(NFCTagReaderSession session, INFCTag[] tags)
        {
            var tag = tags.FirstOrDefault();
            if (tag == null || tag.Type != NFCTagType.MiFare)
            {
                session.InvalidateSession("Invalid tag type.");
                return;
            }

            // MiFare tags implement NFCMiFareTag natively; wrap the same object with that protocol's interface.
            var mifare = Runtime.GetINativeObject<INFCMiFareTag>(tag.Handle, false);
            if (mifare == null)
            {
                session.InvalidateSession("Invalid tag type.");
                return;
            }

            session.ConnectTo(tag, error =>
            {
                if (error != null)
                {
                    session.InvalidateSession(error.LocalizedDescription);
                    return;
                }

                Connection = new IosTagConnection(session, mifare);
                Connected.TrySetResult(Connection);
            });
        }
    }
}

internal sealed class IosTagConnection : INfcTagConnection
{
    private readonly NFCTagReaderSession _session;
    private readonly INFCMiFareTag _tag;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _closed;

    public IosTagConnection(NFCTagReaderSession session, INFCMiFareTag tag)
    {
        _session = session;
        _tag = tag;
        Uid = tag.Identifier?.ToArray() ?? Array.Empty<byte>();
    }

    public byte[] Uid { get; }

    // Matches the Swift app's FAST_READ batch size (0x20 pages).
    public int MaxFastReadPages => 32;

    internal void MarkInvalidated() => _closed = true;

    public async Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            throw new NfcException("The NFC session has ended.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

            _tag.SendMiFareCommand(NSData.FromArray(command), (data, error) =>
            {
                if (error != null)
                {
                    completion.TrySetException(new NfcException(error.LocalizedDescription));
                }
                else
                {
                    completion.TrySetResult(data?.ToArray() ?? Array.Empty<byte>());
                }
            });

            return await completion.Task;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void SetStatus(string message)
    {
        if (!_closed)
        {
            MainThread.BeginInvokeOnMainThread(() => _session.AlertMessage = message);
        }
    }

    public Task CloseAsync(string? errorMessage = null)
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        _closed = true;
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (errorMessage == null)
            {
                _session.InvalidateSession();
            }
            else
            {
                _session.InvalidateSession(errorMessage);
            }
        });
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
