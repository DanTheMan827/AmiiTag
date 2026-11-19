using System.Diagnostics;
using System.Text;

namespace AmiiTag.Bluetooth;

/// <summary>
/// Debug builds log every BLE command and the data exchanged with a device (writes, notifications, reads, service and
/// characteristic lookups, MTU and disconnects) with Debug.WriteLine: Visual Studio's Output window in the apps, the
/// browser console on the web (the web app's debug log forwards it there). Release builds don't include any of it.
/// </summary>
public static class BleTrace
{
    /// <summary>
    /// The connection, logged in debug builds; the connection itself in release builds.
    /// </summary>
    public static IBleConnection Wrap(IBleConnection connection)
    {
#if DEBUG
        return connection is TracingConnection ? connection : new TracingConnection(connection);
#else
        return connection;
#endif
    }

#if DEBUG
    private static void Log(BleDeviceInfo device, string message) =>
        Debug.WriteLine($"BLE [{device.DisplayName}] {message}");

    /// <summary>
    /// Bytes as hex, followed by their printable ASCII where the data is mostly text (Espruino's console, say).
    /// </summary>
    private static string Describe(byte[] data)
    {
        var hex = Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 256)));
        var spaced = new StringBuilder(hex.Length * 3 / 2);
        for (var i = 0; i < hex.Length; i += 2)
        {
            if (i > 0)
            {
                spaced.Append(' ');
            }

            spaced.Append(hex, i, 2);
        }

        if (data.Length > 256)
        {
            spaced.Append($" … (+{data.Length - 256} bytes)");
        }

        var printable = data.Count(b => b is >= 0x20 and < 0x7F or (byte)'\r' or (byte)'\n' or (byte)'\t');
        if (data.Length > 0 && printable * 4 >= data.Length * 3)
        {
            var text = new string(data.Select(b => b is >= 0x20 and < 0x7F ? (char)b : b == '\n' ? '⏎' : '·').ToArray());
            spaced.Append($"  \"{text}\"");
        }

        return $"({data.Length}) {spaced}";
    }

    private static string Name(Guid uuid)
    {
        if (uuid == BleUuids.NordicUartService) return "UART";
        if (uuid == BleUuids.NordicUartWrite) return "UART TX";
        if (uuid == BleUuids.NordicUartNotify) return "UART RX";
        var text = uuid.ToString();
        return text.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ? text[4..8] : text[..8];
    }

    private sealed class TracingConnection : IBleConnection
    {
        private readonly IBleConnection _inner;
        private readonly Dictionary<(Guid, Guid), TracingCharacteristic> _characteristics = [];

        public TracingConnection(IBleConnection inner)
        {
            _inner = inner;
            _inner.Disconnected += OnDisconnected;
            Log(Device, "connected");
        }

        public BleDeviceInfo Device => _inner.Device;

        public bool IsConnected => _inner.IsConnected;

        public event EventHandler? Disconnected;

        private void OnDisconnected(object? sender, EventArgs e)
        {
            Log(Device, "disconnected");
            Disconnected?.Invoke(this, e);
        }

        public async Task<bool> HasServiceAsync(Guid service, CancellationToken cancellationToken = default)
        {
            var found = await _inner.HasServiceAsync(service, cancellationToken);
            Log(Device, $"service {Name(service)}: {(found ? "found" : "not found")}");
            return found;
        }

        public async Task<IBleCharacteristic?> GetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken = default)
        {
            lock (_characteristics)
            {
                if (_characteristics.TryGetValue((service, characteristic), out var cached))
                {
                    return cached;
                }
            }

            var inner = await _inner.GetCharacteristicAsync(service, characteristic, cancellationToken);
            Log(Device, $"characteristic {Name(service)}/{Name(characteristic)}: {(inner != null ? "found" : "not found")}");
            if (inner == null)
            {
                return null;
            }

            lock (_characteristics)
            {
                // One wrapper per characteristic, so each notification is logged once however many listeners it has.
                if (!_characteristics.TryGetValue((service, characteristic), out var wrapper))
                {
                    wrapper = new TracingCharacteristic(Device, inner);
                    _characteristics[(service, characteristic)] = wrapper;
                }

                return wrapper;
            }
        }

        public async Task<int> RequestMtuAsync(int mtu, CancellationToken cancellationToken = default)
        {
            var result = await _inner.RequestMtuAsync(mtu, cancellationToken);
            Log(Device, $"MTU requested {mtu}, got {result}");
            return result;
        }

        public Task ExpectDeviceDisconnectAsync()
        {
            Log(Device, "expecting the device to disconnect");
            return _inner.ExpectDeviceDisconnectAsync();
        }

        public async Task DisconnectAsync()
        {
            Log(Device, "disconnecting");
            await _inner.DisconnectAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _inner.Disconnected -= OnDisconnected;
            await _inner.DisposeAsync();
        }
    }

    private sealed class TracingCharacteristic : IBleCharacteristic
    {
        private readonly BleDeviceInfo _device;
        private readonly IBleCharacteristic _inner;
        private readonly string _name;
        private EventHandler<byte[]>? _valueUpdated;

        public TracingCharacteristic(BleDeviceInfo device, IBleCharacteristic inner)
        {
            _device = device;
            _inner = inner;
            _name = Name(inner.Id);
        }

        public Guid Id => _inner.Id;

        public event EventHandler<byte[]>? ValueUpdated
        {
            add
            {
                lock (this)
                {
                    if (_valueUpdated == null)
                    {
                        _inner.ValueUpdated += OnValueUpdated;
                    }

                    _valueUpdated += value;
                }
            }
            remove
            {
                lock (this)
                {
                    _valueUpdated -= value;
                    if (_valueUpdated == null)
                    {
                        _inner.ValueUpdated -= OnValueUpdated;
                    }
                }
            }
        }

        private void OnValueUpdated(object? sender, byte[] value)
        {
            Log(_device, $"< {_name} {Describe(value)}");
            _valueUpdated?.Invoke(this, value);
        }

        public async Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default)
        {
            Log(_device, $"> {_name}{(withResponse ? string.Empty : " (no response)")} {Describe(data)}");
            try
            {
                await _inner.WriteAsync(data, withResponse, cancellationToken);
            }
            catch (Exception ex)
            {
                Log(_device, $"! {_name} write failed: {ex.Message}");
                throw;
            }
        }

        public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
        {
            var data = await _inner.ReadAsync(cancellationToken);
            Log(_device, $"< {_name} read {Describe(data)}");
            return data;
        }

        public async Task StartNotificationsAsync(CancellationToken cancellationToken = default)
        {
            Log(_device, $"{_name} notifications on");
            await _inner.StartNotificationsAsync(cancellationToken);
        }

        public async Task StopNotificationsAsync(CancellationToken cancellationToken = default)
        {
            Log(_device, $"{_name} notifications off");
            await _inner.StopNotificationsAsync(cancellationToken);
        }
    }
#endif
}
