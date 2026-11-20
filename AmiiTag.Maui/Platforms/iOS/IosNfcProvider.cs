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
            Log($"beginning a session ({options.Prompt})");
            session.BeginSession();
        });

        using var registration = cancellationToken.Register(() =>
        {
            MainThread.BeginInvokeOnMainThread(() => sessionDelegate.Session?.InvalidateSession());
            sessionDelegate.Connected.TrySetCanceled(cancellationToken);
        });

        return await sessionDelegate.Connected.Task;
    }

    private static void Log(string message) => System.Diagnostics.Debug.WriteLine($"iOS NFC: {message}");

    private sealed class SessionDelegate : NFCTagReaderSessionDelegate
    {
        public NFCTagReaderSession? Session
        {
            get; set;
        }

        public TaskCompletionSource<INfcTagConnection> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IosTagConnection? Connection
        {
            get; private set;
        }

        public override void DidBecomeActive(NFCTagReaderSession session) => Log("session active, polling");

        public override void DidInvalidate(NFCTagReaderSession session, NSError error)
        {
            Log($"session invalidated: {error.Domain} {error.Code} {error.LocalizedDescription}" +
                (Connection == null ? " (before a tag was connected)" : string.Empty));
            Connection?.MarkInvalidated(error);

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
            Log($"detected {tags.Length} tag(s): {string.Join(", ", tags.Select(t => t.Type.ToString()))}");
            var tag = tags.FirstOrDefault();
            if (tag == null || tag.Type != NFCTagType.MiFare)
            {
                Log("rejected: not a MIFARE family tag");
                session.InvalidateSession("Invalid tag type.");
                return;
            }

            // MiFare tags implement NFCMiFareTag natively; wrap the same object with that protocol's interface.
            var mifare = Runtime.GetINativeObject<INFCMiFareTag>(tag.Handle, false);
            if (mifare == null)
            {
                Log("rejected: the tag doesn't implement NFCMiFareTag");
                session.InvalidateSession("Invalid tag type.");
                return;
            }

            Log($"MIFARE family {mifare.MifareFamily}, UID {Convert.ToHexString(mifare.Identifier?.ToArray() ?? [])}; connecting");
            session.ConnectTo(tag, error =>
            {
                if (error != null)
                {
                    Log($"connect failed: {error.Domain} {error.Code} {error.LocalizedDescription}");
                    session.InvalidateSession(error.LocalizedDescription);
                    return;
                }

                Log("connected");
                Connection = new IosTagConnection(session, tag, mifare);
                Connected.TrySetResult(Connection);
            });
        }
    }
}

internal sealed class IosTagConnection : INfcTagConnection, INfcReselectable
{
    private readonly NFCTagReaderSession _session;
    private readonly INFCMiFareTag _tag;
    private readonly INFCTag _nativeTag;
    private TaskCompletionSource<byte[]>? _pending;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _closed;

    public IosTagConnection(NFCTagReaderSession session, INFCTag nativeTag, INFCMiFareTag tag)
    {
        _session = session;
        _tag = tag;
        _nativeTag = nativeTag;
        Uid = tag.Identifier?.ToArray() ?? Array.Empty<byte>();
    }

    public byte[] Uid
    {
        get;
    }

    // Matches the Swift app's FAST_READ batch size (0x20 pages).
    public int MaxFastReadPages => 32;

    internal void MarkInvalidated(NSError error)
    {
        _closed = true;
        _pending?.TrySetException(new NfcConnectionLostException("The NFC session has ended.", new NSErrorException(error)));
    }

    public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default) =>
        ExchangeAsync(command, false, cancellationToken);

    public Task<byte[]> TransceiveAsync(byte[] command, TimeSpan responseTimeout, CancellationToken cancellationToken = default) =>
        // Core NFC owns the RF timeout: don't fake a 10 ms deadline and send the next command while this one runs.
        ExchangeAsync(command, command is [<= 1, 0, 0, 0], cancellationToken);

    public Task SendAsync(byte[] command, CancellationToken cancellationToken = default) =>
        ExchangeAsync(command, true, cancellationToken);

    private async Task<byte[]> ExchangeAsync(byte[] command, bool passiveAck, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosed();
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = completion;
            ThrowIfClosed();
            _tag.SendMiFareCommand(NSData.FromArray(command), (data, error) =>
            {
                if (error != null)
                {
                    System.Diagnostics.Debug.WriteLine($"iOS NFC: command {Convert.ToHexString(command.AsSpan(0, Math.Min(command.Length, 3)))}... failed: {error.Domain} {error.Code} {error.LocalizedDescription}");
                }

                if (error == null)
                {
                    completion.TrySetResult(data?.ToArray() ?? Array.Empty<byte>());
                }
                else if (passiveAck && !_closed && error.Domain == "NFCError" && (NFCReaderError)(long)error.Code is
                         NFCReaderError.ReaderTransceiveErrorTagConnectionLost or NFCReaderError.ReaderTransceiveErrorRetryExceeded
                         or NFCReaderError.ReaderTransceiveErrorTagResponseError)
                {
                    // Core NFC can report silence as response-error/no-response (102), lost contact (100),
                    // or retries exceeded (101). Only this known passive-ACK packet accepts those errors.
                    // Silence is the second SECTOR_SELECT packet's acknowledgement. Don't reconnect here:
                    // reactivation can reset the sector; if the NEXT command loses contact, recovery restores it.
                    completion.TrySetResult(Array.Empty<byte>());
                }
                else if ((NFCReaderError)(long)error.Code == NFCReaderError.ReaderTransceiveErrorPacketTooLong)
                {
                    completion.TrySetException(new NfcCommandNotSupportedException(command[0]));
                }
                else if ((NFCReaderError)(long)error.Code == NFCReaderError.ReaderTransceiveErrorTagResponseError)
                {
                    completion.TrySetException(new NfcTagResponseException(error.LocalizedDescription, new NSErrorException(error)));
                }
                else
                {
                    // Session invalidation, tag-not-connected and other native errors must not be swallowed as
                    // a passive ACK by the shared sector-selection code. Keep the native NSError for diagnostics.
                    completion.TrySetException(new NfcConnectionLostException(error.LocalizedDescription, new NSErrorException(error)));
                }
            });

            // Cancellation is observed after the native callback, so the command gate stays held while RF I/O runs.
            var response = await completion.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return response;
        }
        finally
        {
            _pending = null;
            _lock.Release();
        }
    }

    public async Task ReselectAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfClosed();
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = completion;
            ThrowIfClosed();
            // Reconnect the original tag within this session. Never restart polling or start a new RF session.
            // The shared recovery callback reselects the required sector and reauthenticates afterward.
            _session.ConnectTo(_nativeTag, error =>
            {
                if (error != null)
                    completion.TrySetException(new NfcConnectionLostException(error.LocalizedDescription, new NSErrorException(error)));
                else if (!(_tag.Identifier?.ToArray() ?? Array.Empty<byte>()).AsSpan().SequenceEqual(Uid))
                    completion.TrySetException(new NfcConnectionLostException("A different tag is on the reader.", new InvalidOperationException()));
                else
                    completion.TrySetResult(Array.Empty<byte>());
            });
            await completion.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _pending = null;
            _lock.Release();
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new NfcConnectionLostException("The NFC session has ended.", new InvalidOperationException());
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
