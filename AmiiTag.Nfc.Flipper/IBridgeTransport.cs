namespace AmiiTag.Nfc.Flipper;

/// <summary>
/// An open connection to the Flipper's bridge interface: 64 byte reports each way.
/// </summary>
public interface IBridgeTransport : IAsyncDisposable
{
    /// <summary>
    /// How the Flipper is reached (for the log), e.g. "USB bulk" or "USB HID".
    /// </summary>
    string Name => "USB";

    /// <summary>
    /// Sends one or more 64 byte reports (a whole message), back to back. Synchronous on purpose: answers go out
    /// straight from the read loop.
    /// </summary>
    void Write(ReadOnlySpan<byte> reports);

    /// <summary>
    /// The next 64 byte report. Throws (or completes with an exception) when the device goes away.
    /// </summary>
    ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by transports with their own reading thread: reports are handed over on that thread, so an answer
/// goes back without waiting for the thread pool (which a busy app can delay by tens of milliseconds, longer than a
/// reader waits).
/// </summary>
public interface IBridgeReportSource
{
    /// <summary>
    /// Starts reading: <paramref name="onReport"/> for every 64 byte report, then <paramref name="onClosed"/> once
    /// (null when closed on purpose). Use instead of <see cref="IBridgeTransport.ReadAsync"/>.
    /// </summary>
    void StartReports(Action<byte[]> onReport, Action<Exception?> onClosed);
}

/// <summary>
/// Finds and opens a Flipper running the AmiiTag bridge app. Supplied by platforms that can reach USB HID.
/// </summary>
public interface IFlipperTransportProvider
{
    bool IsSupported { get; }

    /// <summary>
    /// Whether a Flipper running the bridge app is plugged in (kept current as devices come and go).
    /// </summary>
    bool IsDevicePresent { get; }

    event EventHandler? DevicesChanged;

    Task<IBridgeTransport?> OpenAsync(CancellationToken cancellationToken = default);
}
