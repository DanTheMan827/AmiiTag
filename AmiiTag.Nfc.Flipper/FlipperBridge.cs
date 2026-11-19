using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace AmiiTag.Nfc.Flipper;

/// <summary>
/// Talks to the Flipper NFC Bridge app (protocol v2): card emulation, reader mode and the answer table. Reader frames
/// are answered with <see cref="Responder"/> straight from the read loop, with no thread hops in between.
/// </summary>
public sealed class FlipperBridge : IAsyncDisposable
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);

    private readonly IBridgeTransport _transport;
    private readonly BridgeAssembler _assembler = new();
    private readonly Lock _writeLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<BridgeTransceiveResult>> _transceives = new();
    private TaskCompletionSource<BridgeHello>? _hello;
    private TaskCompletionSource<BridgeStatus>? _status;
    private TaskCompletionSource<BridgeStatusCode>? _ack;
    private Task? _readLoop;
    private int _transceiveSeq;
    private int _queuedTableSets;

    public FlipperBridge(IBridgeTransport transport) => _transport = transport;

    /// <summary>
    /// Answers a reader frame in listen mode. Runs on the read loop, so it must be quick; an exception stays silent.
    /// Not called for frames the answer table already answered.
    /// </summary>
    public Func<BridgeFrame, BridgeAnswer>? Responder { get; set; }

    /// <summary>
    /// After each frame: the frame, the answer (null when answered from the table or the responder failed) and how long
    /// the host took. Runs on the read loop.
    /// </summary>
    public event Action<BridgeFrame, BridgeAnswer?, TimeSpan>? FrameHandled;

    /// <summary>
    /// FIELD_ON, FIELD_OFF, ACTIVATED or HALTED, with the Flipper's timestamp. Runs on the read loop.
    /// </summary>
    public event Action<BridgeMessageType, uint>? TagEvent;

    public event Action<string>? DeviceLog;

    /// <summary>
    /// The connection ended (null when stopped on purpose).
    /// </summary>
    public event Action<Exception?>? Closed;

    public BridgeHello? Hello { get; private set; }

    public bool IsRunning => _readLoop is { IsCompleted: false };

    public void Start()
    {
        if (_readLoop != null)
        {
            return;
        }

        if (_transport is IBridgeReportSource source)
        {
            // Frames are answered on the transport's own thread: no thread pool hop between a frame and its answer.
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _readLoop = done.Task;
            source.StartReports(OnReport, error =>
            {
                Finish(error);
                done.TrySetResult();
            });
        }
        else
        {
            _readLoop = Task.Run(ReadLoopAsync);
        }
    }

    private void OnReport(byte[] report)
    {
        if (_stop.IsCancellationRequested || !_assembler.TryAdd(report, out var message))
        {
            return;
        }

        try
        {
            Handle(message);
        }
        catch (Exception ex)
        {
            DeviceLog?.Invoke($"Couldn't handle a {message.Type} message: {ex.Message}");
        }
    }

    // ---- control ----

    public async Task<BridgeHello> HelloAsync(CancellationToken cancellationToken = default)
    {
        var hello = _hello = new TaskCompletionSource<BridgeHello>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(BridgeMessageType.HostHello, 0, []);
        BridgeHello result;
        try
        {
            result = await hello.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new NotSupportedException("The Flipper app is too old for this version of AmiiTag. Update the NFC Bridge app.");
        }

        if (result.ProtocolVersion != BridgeProtocol.Version)
        {
            throw new NotSupportedException(
                $"The Flipper app speaks bridge protocol {result.ProtocolVersion}; this version of AmiiTag needs {BridgeProtocol.Version}. Update the NFC Bridge app.");
        }

        return Hello = result;
    }

    public async Task<BridgeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = _status = new TaskCompletionSource<BridgeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(BridgeMessageType.GetStatus, 0, []);
        return await status.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a control message and waits for its ACK. Not for use from the read loop (see <see cref="QueueTableSet"/>).
    /// </summary>
    private async Task ControlAsync(BridgeMessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var ack = _ack = new TaskCompletionSource<BridgeStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
            Send(type, 0, payload.Span);
            var status = await ack.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false);
            if (status != BridgeStatusCode.Ok)
            {
                throw new BridgeCommandException(type, status);
            }
        }
        finally
        {
            _ack = null;
            _controlGate.Release();
        }
    }

    /// <summary>
    /// Card emulation, ISO 14443-A: the Flipper answers anticollision with this UID, ATQA (in air order, NTAG: 44 00)
    /// and SAK, and relays every later frame.
    /// </summary>
    public Task ListenIso14443AAsync(ReadOnlySpan<byte> uid, ReadOnlySpan<byte> atqa, byte sak,
        BridgeListenFlags flags = BridgeListenFlags.Default, TimeSpan? answerTimeout = null, CancellationToken cancellationToken = default)
    {
        if (uid.Length is not (4 or 7 or 10))
        {
            throw new ArgumentException("UIDs are 4, 7 or 10 bytes.", nameof(uid));
        }

        byte[] parameters = [(byte)uid.Length, .. uid, atqa[0], atqa[1], sak];
        return ListenAsync(BridgeTech.Iso14443A, flags, answerTimeout, parameters, cancellationToken);
    }

    /// <summary>
    /// Card emulation, ISO 15693: everything is a frame. Only answer table hits are fast enough for real readers.
    /// </summary>
    public Task ListenIso15693Async(BridgeListenFlags flags = BridgeListenFlags.StripCrc | BridgeListenFlags.NotifyTableHits,
        TimeSpan? answerTimeout = null, CancellationToken cancellationToken = default)
        => ListenAsync(BridgeTech.Iso15693, flags, answerTimeout, [], cancellationToken);

    /// <summary>
    /// Card emulation, FeliCa: the Flipper answers polling with this IDm, PMm and system code.
    /// </summary>
    public Task ListenFelicaAsync(ReadOnlySpan<byte> idm, ReadOnlySpan<byte> pmm, ushort systemCode,
        BridgeListenFlags flags = BridgeListenFlags.StripCrc | BridgeListenFlags.NotifyTableHits,
        TimeSpan? answerTimeout = null, CancellationToken cancellationToken = default)
    {
        if (idm.Length != 8 || pmm.Length != 8)
        {
            throw new ArgumentException("IDm and PMm are 8 bytes each.");
        }

        byte[] parameters = [.. idm, .. pmm, (byte)systemCode, (byte)(systemCode >> 8)];
        return ListenAsync(BridgeTech.Felica, flags, answerTimeout, parameters, cancellationToken);
    }

    private Task ListenAsync(BridgeTech tech, BridgeListenFlags flags, TimeSpan? answerTimeout, byte[] parameters, CancellationToken cancellationToken)
    {
        var timeout = (ushort)Math.Clamp((answerTimeout ?? BridgeProtocol.DefaultAnswerTimeout).TotalMilliseconds, 1, ushort.MaxValue);
        byte[] payload = [(byte)tech, (byte)flags, (byte)timeout, (byte)(timeout >> 8), .. parameters];
        return ControlAsync(BridgeMessageType.Listen, payload, cancellationToken);
    }

    /// <summary>
    /// Reader mode: the field turns on; use <see cref="TransceiveAsync"/>.
    /// </summary>
    public Task ReaderAsync(BridgeTech tech, CancellationToken cancellationToken = default)
        => ControlAsync(BridgeMessageType.Reader, new byte[] { (byte)tech, 0 }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default)
        => ControlAsync(BridgeMessageType.Stop, Array.Empty<byte>(), cancellationToken);

    /// <summary>
    /// Overrides the NFC timings from the next listen/reader start (0 keeps the default). fc = 13.56 MHz cycles.
    /// </summary>
    public Task SetTimingAsync(uint fdtPollFc = 0, uint fdtListenFc = 0, uint guardMicros = 0, uint pollPollMicros = 0,
        uint maskReceiveFc = 0, CancellationToken cancellationToken = default)
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, fdtPollFc);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), fdtListenFc);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), guardMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), pollPollMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), maskReceiveFc);
        return ControlAsync(BridgeMessageType.SetTiming, payload, cancellationToken);
    }

    public Task FieldResetAsync(CancellationToken cancellationToken = default)
        => ControlAsync(BridgeMessageType.FieldReset, Array.Empty<byte>(), cancellationToken);

    // ---- reader ----

    /// <summary>
    /// Sends a frame in reader mode and returns the card's response. Bits defaults to all of data.
    /// </summary>
    public async Task<BridgeTransceiveResult> TransceiveAsync(ReadOnlyMemory<byte> data, BridgeTxFlags flags = BridgeTxFlags.None,
        BridgeTransceiveKind kind = BridgeTransceiveKind.Frame, int? bits = null, TimeSpan? timeout = null,
        ReadOnlyMemory<byte> parity = default, CancellationToken cancellationToken = default)
    {
        var seq = (byte)Interlocked.Increment(ref _transceiveSeq);
        var tcs = new TaskCompletionSource<BridgeTransceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transceives[seq] = tcs;

        var count = bits ?? data.Length * 8;
        var micros = (uint)Math.Clamp((timeout ?? TimeSpan.FromMilliseconds(5)).TotalMicroseconds, 1, uint.MaxValue);
        if (!parity.IsEmpty)
        {
            flags |= BridgeTxFlags.CustomParity;
        }

        var payload = new byte[8 + data.Length + parity.Length];
        payload[0] = (byte)kind;
        payload[1] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), micros);
        data.Span.CopyTo(payload.AsSpan(8));
        parity.Span.CopyTo(payload.AsSpan(8 + data.Length));

        try
        {
            Send(BridgeMessageType.Transceive, seq, payload);
            return await tcs.Task.WaitAsync(ReplyTimeout + TimeSpan.FromMicroseconds(micros), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transceives.TryRemove(seq, out _);
        }
    }

    // ---- answer table ----

    public Task TableClearAsync(CancellationToken cancellationToken = default)
        => ControlAsync(BridgeMessageType.TableClear, Array.Empty<byte>(), cancellationToken);

    /// <summary>
    /// The Flipper answers this exact frame (CRC stripped) on its own from now on.
    /// </summary>
    public Task TableSetAsync(ReadOnlySpan<byte> match, BridgeAnswer answer, CancellationToken cancellationToken = default)
        => ControlAsync(BridgeMessageType.TableSet, EncodeTableEntry(match, answer), cancellationToken);

    public Task TableRemoveAsync(ReadOnlySpan<byte> match, CancellationToken cancellationToken = default)
        => ControlAsync(BridgeMessageType.TableRemove, (byte[])[(byte)match.Length, .. match], cancellationToken);

    /// <summary>
    /// Like <see cref="TableSetAsync"/> without waiting for the ACK: for use from the read loop (a responder or
    /// <see cref="FrameHandled"/>), which can't wait for its own replies.
    /// </summary>
    public void QueueTableSet(ReadOnlySpan<byte> match, BridgeAnswer answer)
    {
        var payload = EncodeTableEntry(match, answer);
        Interlocked.Increment(ref _queuedTableSets);
        Send(BridgeMessageType.TableSet, 0, payload);
    }

    private static byte[] EncodeTableEntry(ReadOnlySpan<byte> match, BridgeAnswer answer)
    {
        if (match.Length is 0 or > 255)
        {
            throw new ArgumentException("Matches are 1 to 255 bytes.", nameof(match));
        }

        var encoded = answer.Encode(); // flags, bits, data, [parity]
        var payload = new byte[4 + match.Length + encoded.Length - 3];
        payload[0] = encoded[0];
        payload[1] = (byte)match.Length;
        payload[2] = encoded[1];
        payload[3] = encoded[2];
        match.CopyTo(payload.AsSpan(4));
        encoded.AsSpan(3).CopyTo(payload.AsSpan(4 + match.Length));
        return payload;
    }

    // ---- plumbing ----

    private void Send(BridgeMessageType type, byte sequence, ReadOnlySpan<byte> payload)
    {
        var reports = BridgeFraming.Encode(type, sequence, payload);
        var message = reports.Count == 1 ? reports[0] : reports.SelectMany(report => report).ToArray();
        lock (_writeLock)
        {
            // The whole message in one write: over bulk its packets leave in the same millisecond.
            _transport.Write(message);
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var report = await _transport.ReadAsync(_stop.Token).ConfigureAwait(false);
                if (_assembler.TryAdd(report, out var message))
                {
                    Handle(message);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Finish(error);
    }

    private int _finished;

    private void Finish(Exception? error)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        var failure = error ?? new OperationCanceledException();
        _hello?.TrySetException(failure);
        _status?.TrySetException(failure);
        _ack?.TrySetException(failure);
        foreach (var pending in _transceives.Values)
        {
            pending.TrySetException(failure);
        }

        Closed?.Invoke(error);
    }

    private void Handle(BridgeMessage message)
    {
        switch (message.Type)
        {
            case BridgeMessageType.Frame:
                OnFrame(BridgeFrame.Parse(message));
                break;

            case BridgeMessageType.TransceiveResult:
                if (_transceives.TryGetValue(message.Sequence, out var pending))
                {
                    pending.TrySetResult(BridgeTransceiveResult.Parse(message.Payload));
                }

                break;

            case BridgeMessageType.FieldOn:
            case BridgeMessageType.FieldOff:
            case BridgeMessageType.Activated:
            case BridgeMessageType.Halted:
                TagEvent?.Invoke(message.Type, message.Payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(message.Payload) : 0);
                break;

            case BridgeMessageType.Ack:
                // ACKs of queued table updates arrive with nobody waiting; only the awaited type completes.
                if (message.Payload.Length >= 2 && !IsQueuedAck(message.Payload[0]) && _ack is { } ack)
                {
                    ack.TrySetResult((BridgeStatusCode)message.Payload[1]);
                }

                break;

            case BridgeMessageType.Hello:
                try
                {
                    _hello?.TrySetResult(BridgeHello.Parse(message.Payload));
                }
                catch (Exception ex)
                {
                    _hello?.TrySetException(ex);
                }

                break;

            case BridgeMessageType.Status:
                _status?.TrySetResult(BridgeStatus.Parse(message.Payload));
                break;

            case BridgeMessageType.Log:
                DeviceLog?.Invoke(Encoding.ASCII.GetString(message.Payload));
                break;
        }
    }

    private bool IsQueuedAck(byte type)
    {
        // Queued TABLE_SETs are acknowledged in order; skip that many TABLE_SET ACKs.
        if (type == (byte)BridgeMessageType.TableSet && Volatile.Read(ref _queuedTableSets) > 0)
        {
            Interlocked.Decrement(ref _queuedTableSets);
            return true;
        }

        return false;
    }

    private void OnFrame(BridgeFrame frame)
    {
        if (frame.FromTable)
        {
            FrameHandled?.Invoke(frame, null, TimeSpan.Zero);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        BridgeAnswer? answer;
        try
        {
            answer = Responder?.Invoke(frame) ?? BridgeAnswer.Silence;
        }
        catch (Exception ex)
        {
            DeviceLog?.Invoke($"Responder failed: {ex.Message}");
            answer = null;
        }

        // The answer carries the frame's sequence number so the Flipper can drop answers that come too late.
        Send(BridgeMessageType.Answer, frame.Sequence, (answer ?? BridgeAnswer.Silence).Encode());
        FrameHandled?.Invoke(frame, answer, Stopwatch.GetElapsedTime(started));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Send(BridgeMessageType.Stop, 0, []);
        }
        catch
        {
            // Unplugged already.
        }

        _stop.Cancel();
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_readLoop != null)
        {
            await _readLoop.ConfigureAwait(false);
        }

        _stop.Dispose();
        _controlGate.Dispose();
    }
}

/// <summary>
/// The Flipper refused a control message.
/// </summary>
public sealed class BridgeCommandException(BridgeMessageType type, BridgeStatusCode status)
    : Exception($"The Flipper refused {type}: {status}.")
{
    public BridgeMessageType Type { get; } = type;

    public BridgeStatusCode Status { get; } = status;
}
