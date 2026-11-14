namespace AmiiTag.Nfc.Flipper;

/// <summary>
/// An open connection to the Flipper's bridge interface: 64 byte reports each way.
/// </summary>
public interface IBridgeTransport : IAsyncDisposable
{
    /// <summary>
    /// Sends one 64 byte report. Synchronous on purpose: answers go out straight from the read loop.
    /// </summary>
    void Write(ReadOnlySpan<byte> report);

    /// <summary>
    /// The next 64 byte report. Throws (or completes with an exception) when the device goes away.
    /// </summary>
    ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken);
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
