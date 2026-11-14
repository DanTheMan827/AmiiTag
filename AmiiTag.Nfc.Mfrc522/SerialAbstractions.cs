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
