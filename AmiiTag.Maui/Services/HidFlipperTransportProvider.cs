#if WINDOWS || MACCATALYST
using System.Threading.Channels;
using AmiiTag.Nfc.Flipper;
using HidSharp;

namespace AmiiTag.Maui.Services;

/// <summary>
/// Finds the Flipper NFC Bridge app's vendor HID interface (no driver needed) with HidSharp, and keeps
/// <see cref="IsDevicePresent"/> current as devices are plugged in and out.
/// </summary>
public sealed class HidFlipperTransportProvider : IFlipperTransportProvider
{
    private static readonly uint BridgeUsage = ((uint)BridgeProtocol.UsagePage << 16) | BridgeProtocol.Usage;

    private volatile bool _present;

    public HidFlipperTransportProvider()
    {
        try
        {
            DeviceList.Local.Changed += (_, _) => Refresh();
            Refresh();
        }
        catch
        {
            // No HID access (sandbox): stays unavailable.
        }
    }

    public bool IsSupported => true;

    public bool IsDevicePresent => _present;

    public event EventHandler? DevicesChanged;

    private void Refresh()
    {
        var present = FindDevice() != null;
        if (present != _present)
        {
            _present = present;
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static HidDevice? FindDevice()
    {
        try
        {
            return DeviceList.Local.GetHidDevices(BridgeProtocol.VendorId, BridgeProtocol.ProductId).FirstOrDefault(IsBridge);
        }
        catch
        {
            return null;
        }
    }

    // pid.codes' test PID is shared, so also check the product string or the vendor usage.
    private static bool IsBridge(HidDevice device)
    {
        try
        {
            if (device.GetMaxOutputReportLength() < BridgeProtocol.ReportSize || device.GetMaxInputReportLength() < BridgeProtocol.ReportSize)
            {
                return false;
            }

            if (string.Equals(device.GetProductName(), BridgeProtocol.ProductName, StringComparison.Ordinal))
            {
                return true;
            }

            return device.GetReportDescriptor().DeviceItems.Any(item => item.Usages.GetAllValues().Contains(BridgeUsage));
        }
        catch
        {
            return false;
        }
    }

    public Task<IBridgeTransport?> OpenAsync(CancellationToken cancellationToken = default) => Task.Run<IBridgeTransport?>(() =>
    {
        var device = FindDevice();
        return device != null && device.TryOpen(out var stream) ? new HidSharpBridgeTransport(device, stream) : null;
    }, cancellationToken);

    private sealed class HidSharpBridgeTransport : IBridgeTransport
    {
        private readonly HidStream _stream;
        private readonly int _inputLength;
        private readonly byte[] _output;
        private readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly Thread _reader;
        private volatile bool _closed;

        public HidSharpBridgeTransport(HidDevice device, HidStream stream)
        {
            _stream = stream;
            _stream.ReadTimeout = Timeout.Infinite;
            _stream.WriteTimeout = 100;
            _inputLength = device.GetMaxInputReportLength();
            _output = new byte[device.GetMaxOutputReportLength()];

            // A dedicated thread blocking on reads: the lowest latency HidSharp offers.
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Flipper HID", Priority = ThreadPriority.AboveNormal };
            _reader.Start();
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
                    _reports.Writer.TryWrite(report);
                }

                _reports.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _reports.Writer.TryComplete(_closed ? null : ex);
            }
        }

        public void Write(ReadOnlySpan<byte> report)
        {
            lock (_output)
            {
                Array.Clear(_output);
                var offset = _output.Length > BridgeProtocol.ReportSize ? 1 : 0;
                report[..Math.Min(report.Length, BridgeProtocol.ReportSize)].CopyTo(_output.AsSpan(offset));
                _stream.Write(_output, 0, _output.Length);
            }
        }

        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken) => _reports.Reader.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            _closed = true;
            _stream.Dispose();
            _reports.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
#endif
