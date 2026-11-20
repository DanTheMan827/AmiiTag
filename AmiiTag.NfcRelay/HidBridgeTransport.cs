using System.Threading.Channels;
using AmiiTag.Nfc.Flipper;
using HidSharp;

namespace AmiiTag.NfcRelay;

/// <summary>
/// The Flipper NFC Bridge app's vendor HID interface through HidSharp (no driver needed): the app's HID transport,
/// trimmed.
/// </summary>
internal sealed class HidBridgeTransport : IBridgeTransport, IBridgeReportSource
{
    private static readonly uint BridgeUsage = ((uint)BridgeProtocol.UsagePage << 16) | BridgeProtocol.Usage;

    private readonly HidStream _stream;
    private readonly int _inputLength;
    private readonly byte[] _output;
    private readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly Thread _reader;
    private volatile bool _closed;
    private Action<byte[]>? _onReport;
    private Action<Exception?>? _onClosed;
    private int _started;

    private HidBridgeTransport(HidDevice device, HidStream stream)
    {
        _stream = stream;
        _stream.ReadTimeout = Timeout.Infinite;
        _stream.WriteTimeout = 100;
        _inputLength = device.GetMaxInputReportLength();
        _output = new byte[device.GetMaxOutputReportLength()];
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Flipper HID", Priority = ThreadPriority.Highest };
    }

    public static Task<HidBridgeTransport?> OpenAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var device = DeviceList.Local.GetHidDevices(BridgeProtocol.VendorId, BridgeProtocol.ProductId).FirstOrDefault(IsBridge);
        return device != null && device.TryOpen(out var stream) ? new HidBridgeTransport(device, stream) : null;
    }, cancellationToken);

    // pid.codes' test PID is shared, so also check the product string or the vendor usage.
    private static bool IsBridge(HidDevice device)
    {
        try
        {
            if (device.GetMaxOutputReportLength() < BridgeProtocol.ReportSize || device.GetMaxInputReportLength() < BridgeProtocol.ReportSize)
            {
                return false;
            }

            return string.Equals(device.GetProductName(), BridgeProtocol.ProductName, StringComparison.Ordinal) ||
                device.GetReportDescriptor().DeviceItems.Any(item => item.Usages.GetAllValues().Contains(BridgeUsage));
        }
        catch
        {
            return false;
        }
    }

    public string Name => "USB HID";

    public void StartReports(Action<byte[]> onReport, Action<Exception?> onClosed)
    {
        _onReport = onReport;
        _onClosed = onClosed;
        EnsureStarted();
    }

    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _reader.Start();
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[_inputLength];
        try
        {
            while (!_closed)
            {
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    continue;
                }

                // HidSharp puts the report ID (0 here) first.
                var offset = read > BridgeProtocol.ReportSize ? 1 : 0;
                var report = new byte[BridgeProtocol.ReportSize];
                buffer.AsSpan(offset, Math.Min(BridgeProtocol.ReportSize, read - offset)).CopyTo(report);
                if (_onReport is { } onReport)
                {
                    onReport(report);
                }
                else
                {
                    _reports.Writer.TryWrite(report);
                }
            }

            Close(null);
        }
        catch (Exception ex)
        {
            Close(_closed ? null : ex);
        }
    }

    private void Close(Exception? error)
    {
        _reports.Writer.TryComplete(error);
        _onClosed?.Invoke(error);
    }

    public void Write(ReadOnlySpan<byte> reports)
    {
        lock (_output)
        {
            for (var start = 0; start < reports.Length; start += BridgeProtocol.ReportSize)
            {
                var report = reports.Slice(start, Math.Min(BridgeProtocol.ReportSize, reports.Length - start));
                Array.Clear(_output);
                var offset = _output.Length > BridgeProtocol.ReportSize ? 1 : 0;
                report.CopyTo(_output.AsSpan(offset));
                _stream.Write(_output, 0, _output.Length);
            }
        }
    }

    public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        return _reports.Reader.ReadAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _closed = true;
        _stream.Dispose();
        _reports.Writer.TryComplete();
        if (Volatile.Read(ref _started) == 0)
        {
            _onClosed?.Invoke(null);
        }

        return ValueTask.CompletedTask;
    }
}
