using System.IO.Ports;
using System.Threading.Channels;
using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;

namespace AmiiTag.Maui.NfcProviders;

/// <summary>
/// COM ports through System.IO.Ports, for an MFRC522 on a USB serial adapter (driven directly in UART mode).
/// </summary>
public sealed class SystemSerialPortFactory : ISerialPortFactory
{
    public bool IsSupported => true;

    public Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NfcReaderInfo> ports = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new NfcReaderInfo(name, name))
            .ToList();
        return Task.FromResult(ports);
    }

    public Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default)
    {
        var port = new SerialPort(portId, baudRate)
        {
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true,
        };

        try
        {
            port.Open();
        }
        catch (Exception ex)
        {
            port.Dispose();
            throw new NfcException($"Couldn't open {portId}: {ex.Message}", ex);
        }

        return Task.FromResult<ISerialPortStream>(new SystemSerialPortStream(port));
    }

    /// <summary>
    /// Reads on a background loop so cancellation never leaves an orphaned read on the port.
    /// </summary>
    private sealed class SystemSerialPortStream : ISerialPortStream
    {
        private readonly SerialPort _port;
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        private byte[]? _pending;
        private int _pendingOffset;

        public SystemSerialPortStream(SerialPort port)
        {
            _port = port;
            _ = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[256];
            try
            {
                while (_port.IsOpen)
                {
                    var read = await _port.BaseStream.ReadAsync(buffer);
                    if (read <= 0)
                    {
                        break;
                    }

                    _received.Writer.TryWrite(buffer.AsSpan(0, read).ToArray());
                }
            }
            catch
            {
                // Port closed or unplugged.
            }
            finally
            {
                _received.Writer.TryComplete();
            }
        }

        public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            await _port.BaseStream.WriteAsync(data, cancellationToken);
            await _port.BaseStream.FlushAsync(cancellationToken);
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            if (_pending == null)
            {
                try
                {
                    _pending = await _received.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    throw new IOException("The serial port was closed.");
                }

                _pendingOffset = 0;
            }

            var take = Math.Min(count, _pending.Length - _pendingOffset);
            Array.Copy(_pending, _pendingOffset, buffer, offset, take);
            _pendingOffset += take;
            if (_pendingOffset >= _pending.Length)
            {
                _pending = null;
            }

            return take;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _port.Close();
            }
            catch
            {
            }

            _port.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
