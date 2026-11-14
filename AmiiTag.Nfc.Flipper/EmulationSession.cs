using System.Runtime;
using System.Text;
using AmiiTag.Simulation;

namespace AmiiTag.Nfc.Flipper;

public enum EmulationLogKind
{
    Info,
    Reader,
    Tag,
    FieldOff,
    Halted,
    Device,
    Error,
}

/// <summary>
/// One line of the traffic log. <see cref="DeviceMicros"/> is the Flipper's clock (0 for host events).
/// </summary>
public sealed record EmulationLogEntry(DateTimeOffset Time, uint DeviceMicros, EmulationLogKind Kind, byte[] Data, string Text)
{
    public string Format()
    {
        var hex = Data.Length > 0 ? Convert.ToHexString(Data) : string.Empty;
        var arrow = Kind switch
        {
            EmulationLogKind.Reader => "R>",
            EmulationLogKind.Tag => "<T",
            EmulationLogKind.Error => "!!",
            _ => "--",
        };
        var clock = DeviceMicros != 0 ? $"{DeviceMicros / 1000.0,10:F3}" : new string(' ', 10);
        return $"{Time:HH:mm:ss.fff} {clock} {arrow} {hex,-40} {Text}".TrimEnd();
    }
}

/// <summary>
/// The traffic log of one session; thread safe, capped at <see cref="Capacity"/> entries.
/// </summary>
public sealed class EmulationLog
{
    public const int Capacity = 50_000;

    private readonly List<EmulationLogEntry> _entries = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Raised from the bridge's read loop: handlers must be quick.
    /// </summary>
    public event Action<EmulationLogEntry>? Added;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public void Add(EmulationLogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count >= Capacity)
            {
                _entries.RemoveRange(0, Capacity / 10);
            }

            _entries.Add(entry);
        }

        Added?.Invoke(entry);
    }

    public void Add(EmulationLogKind kind, string text, byte[]? data = null, uint deviceMicros = 0) =>
        Add(new EmulationLogEntry(DateTimeOffset.Now, deviceMicros, kind, data ?? [], text));

    public IReadOnlyList<EmulationLogEntry> Tail(int count)
    {
        lock (_lock)
        {
            var start = Math.Max(0, _entries.Count - count);
            return _entries.GetRange(start, _entries.Count - start);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public string ToText(string? header = null)
    {
        var text = new StringBuilder();
        if (header != null)
        {
            text.AppendLine(header);
            text.AppendLine();
        }

        text.AppendLine("Time         Flipper ms    Data                                     Meaning");
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                text.AppendLine(entry.Format());
            }
        }

        return text.ToString();
    }
}

/// <summary>
/// The Flipper emulating one simulated tag for a real reader, with every exchange logged.
/// </summary>
/// <remarks>
/// Every frame is relayed to the model by default, so the log shows exactly what the reader sent and what the model
/// answered. For readers too impatient for a USB round trip, <c>useAnswerTable</c> preloads an NTAG215's answers
/// that only change on writes (READ, GET_VERSION, READ_SIG) into the Flipper, which then answers those itself; the
/// session updates them after each write it handles.
/// </remarks>
public sealed class EmulationSession : IAsyncDisposable
{
    private readonly FlipperBridge _bridge;
    private readonly TagSimulatorResponder _responder;
    private readonly bool _useAnswerTable;
    private int _frames;
    private int _answered;
    private int _tableHits;
    private double _worstHostMicros;
    private int? _pendingCompatibilityWrite;

    private string _transportName = "USB";
    private GCLatencyMode? _previousLatency;

    private EmulationSession(FlipperBridge bridge, TagSimulator tag, string tagName, bool useAnswerTable)
    {
        _bridge = bridge;
        _responder = new TagSimulatorResponder(tag);
        _useAnswerTable = useAnswerTable && tag is Ntag215Simulator;
        TagName = tagName;
    }

    public string TagName { get; }

    public TagSimulator Tag => _responder.Tag;

    public BridgeHello? Hello { get; private set; }

    public EmulationLog Log { get; } = new();

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Whether a reader's field is on (as far as the field events and frames tell).
    /// </summary>
    public bool FieldPresent { get; private set; }

    public int Frames => _frames;

    public int Answered => _answered;

    /// <summary>
    /// Frames the Flipper answered from its answer table.
    /// </summary>
    public int TableHits => _tableHits;

    /// <summary>
    /// Whether static answers were preloaded into the Flipper.
    /// </summary>
    public bool UsesAnswerTable => _useAnswerTable;

    /// <summary>
    /// The slowest the host was to produce an answer (the USB round trip comes on top).
    /// </summary>
    public TimeSpan WorstHostTime => TimeSpan.FromMicroseconds(_worstHostMicros);

    public BridgeStatus? DeviceStatus { get; private set; }

    public Exception? Error { get; private set; }

    /// <summary>
    /// Something changed (raised from the read loop; throttle UI updates).
    /// </summary>
    public event Action? Changed;

    public static async Task<EmulationSession> StartAsync(IBridgeTransport transport, TagSimulator tag, string tagName,
        TimeSpan? answerTimeout = null, bool useAnswerTable = false, CancellationToken cancellationToken = default)
    {
        var bridge = new FlipperBridge(transport);
        var session = new EmulationSession(bridge, tag, tagName, useAnswerTable) { _transportName = transport.Name };
        try
        {
            await session.RunAsync(answerTimeout, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await bridge.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunAsync(TimeSpan? answerTimeout, CancellationToken cancellationToken)
    {
        _bridge.Responder = _responder.Respond;
        _bridge.FrameHandled += OnFrameHandled;
        _bridge.TagEvent += OnTagEvent;
        _bridge.DeviceLog += text => Log.Add(EmulationLogKind.Device, text);
        _bridge.Closed += OnClosed;
        _bridge.Start();

        // A blocking (gen 2) collection pauses every thread, the one answering the reader included, for longer than
        // a reader waits. Keep them to the background while emulating.
        _previousLatency = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        Hello = await _bridge.HelloAsync(cancellationToken).ConfigureAwait(false);
        if (!Hello.Capabilities.HasFlag(BridgeCapabilities.ListenA))
        {
            throw new NotSupportedException("This Flipper firmware can't emulate ISO 14443-A cards.");
        }

        await _bridge.TableClearAsync(cancellationToken).ConfigureAwait(false);
        var preloaded = 0;
        if (_useAnswerTable)
        {
            foreach (var (match, answer) in StaticAnswers())
            {
                await _bridge.TableSetAsync(match, answer, cancellationToken).ConfigureAwait(false);
                preloaded++;
            }
        }

        var uid = Tag.Uid7;
        await _bridge.ListenIso14443AAsync(uid, Tag.Atqa, Tag.Sak, BridgeListenFlags.Default, answerTimeout, cancellationToken).ConfigureAwait(false);
        IsRunning = true;

        Log.Add(EmulationLogKind.Info, $"{Hello.Firmware} (protocol {Hello.ProtocolVersion}, over {_transportName})");
        Log.Add(EmulationLogKind.Info,
            $"Emulating {TagName}: UID {Convert.ToHexString(uid)}, ATQA {Convert.ToHexString(Tag.Atqa)}, SAK {Tag.Sak:X2} ({Tag.GetType().Name})");
        if (preloaded > 0)
        {
            Log.Add(EmulationLogKind.Info, $"{preloaded} static answers preloaded into the Flipper (marked \"Flipper\" below)");
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The NTAG215's answers that only change on writes: GET_VERSION, READ_SIG and every READ.
    /// </summary>
    private IEnumerable<(byte[] Match, BridgeAnswer Answer)> StaticAnswers()
    {
        if (Tag is not Ntag215Simulator)
        {
            yield break;
        }

        foreach (var command in new byte[][] { [Ntag215Simulator.CmdGetVersion], [Ntag215Simulator.CmdReadSig, 0x00] })
        {
            if (Ask(command) is { } answer)
            {
                yield return (command, answer);
            }
        }

        for (var page = 0; page <= Ntag215Simulator.LastPage; page++)
        {
            if (ReadAnswer(page) is { } answer)
            {
                yield return ([Ntag215Simulator.CmdRead, (byte)page], answer);
            }
        }
    }

    private BridgeAnswer? ReadAnswer(int page) => Ask([Ntag215Simulator.CmdRead, (byte)page]);

    /// <summary>
    /// What the model answers right now, if it's data (protected pages NAK and stay relayed).
    /// </summary>
    private BridgeAnswer? Ask(byte[] command)
    {
        var response = Tag.Transceive(command);
        if (Tag.IsHalted)
        {
            Tag.Activate();
        }

        if (Tag.CommandLog.Count > 0)
        {
            Tag.CommandLog.RemoveAt(Tag.CommandLog.Count - 1);
        }

        return response.Kind == TagResponseKind.Data ? BridgeAnswer.Frame(response.Data) : null;
    }

    /// <summary>
    /// After a write the model handled, the READ answers covering that page change (READ n returns pages n to n+3,
    /// wrapping around at the end).
    /// </summary>
    private void RefreshAfterWrite(int page)
    {
        const int pages = Ntag215Simulator.LastPage + 1;
        for (var i = 0; i < 4; i++)
        {
            var start = (page - i + pages) % pages;
            if (ReadAnswer(start) is { } answer)
            {
                _bridge.QueueTableSet([Ntag215Simulator.CmdRead, (byte)start], answer);
            }
        }
    }

    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return;
        }

        DeviceStatus = await _bridge.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    private void OnFrameHandled(BridgeFrame frame, BridgeAnswer? answer, TimeSpan hostTime)
    {
        Interlocked.Increment(ref _frames);
        FieldPresent = true;

        var readerText = frame.IsStandard ? NfcCommandNames.Describe(frame.Data) : $"{frame.BitCount} bits, no valid CRC";
        if (frame.FromTable)
        {
            Interlocked.Increment(ref _tableHits);
            Log.Add(EmulationLogKind.Reader, readerText, frame.Data, frame.TimestampMicros);
            Log.Add(EmulationLogKind.Tag, "answered by the Flipper (answer table)");
            Changed?.Invoke();
            return;
        }

        var micros = hostTime.TotalMicroseconds;
        if (micros > _worstHostMicros)
        {
            _worstHostMicros = micros;
        }

        Log.Add(EmulationLogKind.Reader, readerText, frame.Data, frame.TimestampMicros);

        if (answer is not { } a)
        {
            Log.Add(EmulationLogKind.Error, "Responder failed; stayed silent");
        }
        else
        {
            if (!a.IsSilent)
            {
                Interlocked.Increment(ref _answered);
            }

            Log.Add(EmulationLogKind.Tag, DescribeAnswer(a, micros), a.Bits >= 8 ? a.Data : []);

            if (_useAnswerTable && frame.IsStandard)
            {
                TrackWrites(frame.Data, a);
            }
        }

        Changed?.Invoke();
    }

    private void TrackWrites(byte[] command, BridgeAnswer answer)
    {
        var acked = answer is { Bits: 4, Data: [TagResponse.AckValue] };
        if (!acked)
        {
            _pendingCompatibilityWrite = null;
            return;
        }

        if (command is [Ntag215Simulator.CmdWrite, var page, _, _, _, _])
        {
            RefreshAfterWrite(page);
        }
        else if (command is [Ntag215Simulator.CmdCompatibilityWrite, var first])
        {
            _pendingCompatibilityWrite = first;
            return;
        }
        else if (_pendingCompatibilityWrite is { } pending && command.Length == 16)
        {
            RefreshAfterWrite(pending);
        }

        _pendingCompatibilityWrite = null;
    }

    private static string DescribeAnswer(BridgeAnswer answer, double hostMicros)
    {
        var sleep = answer.Flags.HasFlag(BridgeTxFlags.Sleep) ? ", then idle" : string.Empty;
        var what = answer.Bits switch
        {
            0 => "(silent)",
            4 => answer.Data is [TagResponse.AckValue] ? "ACK" : $"NAK {answer.Data[0]:X}",
            _ => $"{answer.Data.Length} bytes{(answer.Flags.HasFlag(BridgeTxFlags.AddCrc) ? string.Empty : " (raw)")}",
        };
        return $"{what}{sleep}  [{hostMicros:F0} µs]";
    }

    private void OnTagEvent(BridgeMessageType type, uint micros)
    {
        switch (type)
        {
            case BridgeMessageType.FieldOn:
                FieldPresent = true;
                Log.Add(EmulationLogKind.FieldOff, "Field on", null, micros);
                break;
            case BridgeMessageType.FieldOff:
                FieldPresent = false;
                _responder.FieldOff();
                Log.Add(EmulationLogKind.FieldOff, "Field off", null, micros);
                break;
            case BridgeMessageType.Activated:
                FieldPresent = true;
                _responder.Activated();
                Log.Add(EmulationLogKind.Halted, "Selected (anticollision done by the Flipper)", null, micros);
                break;
            case BridgeMessageType.Halted:
                _responder.Halted();
                Log.Add(EmulationLogKind.Halted, "HLTA", null, micros);
                break;
        }

        Changed?.Invoke();
    }

    private void OnClosed(Exception? error)
    {
        IsRunning = false;
        FieldPresent = false;
        if (error != null)
        {
            Error = error;
            Log.Add(EmulationLogKind.Error, $"Connection to the Flipper lost: {error.Message}");
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
        {
            Log.Add(EmulationLogKind.Info, "Stopped");
        }

        IsRunning = false;
        if (_previousLatency is { } latency)
        {
            GCSettings.LatencyMode = latency;
            _previousLatency = null;
        }

        await _bridge.DisposeAsync().ConfigureAwait(false);
    }
}
