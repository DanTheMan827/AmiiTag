namespace AmiiTag.Nfc.Mfrc522;

/// <summary>
/// Opens serial ports. Implemented with System.IO.Ports on desktop and Web Serial in the browser.
/// </summary>
public interface ISerialPortFactory
{
    /// <summary>
    /// True if serial ports can be used on this platform.
    /// </summary>
    bool IsSupported { get; }

    Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default);

    Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by factories that need the user to grant access to a port first (Web Serial).
/// </summary>
public interface ISerialPortRequester
{
    Task<NfcReaderInfo?> RequestPortAsync(CancellationToken cancellationToken = default);
}

public interface ISerialPortStream : IAsyncDisposable
{
    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads at least one byte, waiting until data arrives.
    /// </summary>
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional request/reply operation executed beside the serial reader (e.g. in JavaScript), so a register batch
/// does not need a managed callback for each USB chunk. It finishes or drains late replies before returning.
/// Cancellation is intentionally observed by the caller between batches, not during an outstanding UART request.
/// </summary>
public interface ISerialPortTransactionStream : ISerialPortStream
{
    Task<byte[]> TransactAsync(byte[] request, int responseLength, TimeSpan timeout);
}
