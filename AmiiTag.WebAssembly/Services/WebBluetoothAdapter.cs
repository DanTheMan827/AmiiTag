using AmiiTag.Bluetooth;
using Microsoft.JSInterop;

namespace AmiiTag.WebAssembly.Services;

/// <summary>
/// <see cref="IBleAdapter"/> on top of WebBluetooth (Chrome/Edge on desktop and Android, Bluefy on iOS).
/// Browsers can't scan in the background, so devices are added through the browser's picker.
/// </summary>
public sealed class WebBluetoothAdapter : IBleAdapter
{
    private readonly IJSRuntime _js;
    private bool? _available;

    public WebBluetoothAdapter(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// True when the browser has WebBluetooth (and, once checked, a Bluetooth adapter).
    /// </summary>
    public bool IsAvailable => _available ?? (_apiPresent ??= ((IJSInProcessRuntime)_js).Invoke<bool>("amiitag.bluetooth.isSupported"));

    private bool? _apiPresent;

    public bool CanScan => false;

    public bool CanRequestDevice => true;

    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;

    public async Task<bool> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _available = await _js.InvokeAsync<bool>("amiitag.bluetooth.isAvailable", cancellationToken);
        }
        catch (JSException)
        {
            _available = false;
        }

        return _available.Value;
    }

    public async Task<string?> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _available = await _js.InvokeAsync<bool>("amiitag.bluetooth.isAvailable", cancellationToken);
        return _available == true
            ? null
            : "WebBluetooth isn't available. Use Chrome or Edge on desktop or Android (Bluefy on iOS), over HTTPS.";
    }

    public Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Browsers can't scan for devices; use RequestDeviceAsync.");

    public Task StopScanAsync() => Task.CompletedTask;

    public async Task<BleDeviceInfo?> RequestDeviceAsync(BleScanFilter filter, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            services = filter.Services.Select(ToJs).ToArray(),
            names = filter.Names.ToArray(),
            namePrefixes = filter.NamePrefixes.ToArray(),
            optionalServices = filter.OptionalServices.Concat(filter.Services).Distinct().Select(ToJs).ToArray(),
        };

        JsDevice? device;
        try
        {
            device = await _js.InvokeAsync<JsDevice?>("amiitag.bluetooth.requestDevice", cancellationToken, request);
        }
        catch (JSException ex)
        {
            throw new BleException(ex.Message, ex);
        }

        if (device == null)
        {
            return null;
        }

        var info = ToInfo(device);
        DeviceDiscovered?.Invoke(this, info);
        return info;
    }

    public async Task<IReadOnlyList<BleDeviceInfo>> GetKnownDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var devices = await _js.InvokeAsync<JsDevice[]>("amiitag.bluetooth.getDevices", cancellationToken);
            return devices.Select(ToInfo).ToList();
        }
        catch (JSException)
        {
            return [];
        }
    }

    public async Task<IBleConnection> ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
    {
        var connection = new WebBluetoothConnection(_js, device);
        try
        {
            await _js.InvokeVoidAsync("amiitag.bluetooth.connect", cancellationToken, device.Id, connection.Reference);
        }
        catch (JSException ex)
        {
            connection.Reference.Dispose();
            throw new BleException($"Couldn't connect to {device.DisplayName}: {ex.Message}", ex);
        }

        return connection;
    }

    internal static string ToJs(Guid uuid) => uuid.ToString("D");

    private static BleDeviceInfo ToInfo(JsDevice device) => new(device.Id, device.Name, []);

    private sealed record JsDevice(string Id, string? Name);
}

internal sealed class WebBluetoothConnection : IBleConnection
{
    private readonly IJSRuntime _js;
    private readonly Dictionary<(Guid, Guid), WebBluetoothCharacteristic> _characteristics = new();

    public WebBluetoothConnection(IJSRuntime js, BleDeviceInfo device)
    {
        _js = js;
        Device = device;
        Reference = DotNetObjectReference.Create(this);
    }

    public DotNetObjectReference<WebBluetoothConnection> Reference { get; }

    public BleDeviceInfo Device { get; }

    public bool IsConnected { get; private set; } = true;

    public event EventHandler? Disconnected;

    [JSInvokable]
    public void OnDisconnected()
    {
        if (!IsConnected)
        {
            return;
        }

        IsConnected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> HasServiceAsync(Guid service, CancellationToken cancellationToken = default) =>
        await _js.InvokeAsync<bool>("amiitag.bluetooth.hasService", cancellationToken, Device.Id, WebBluetoothAdapter.ToJs(service));

    public async Task<IBleCharacteristic?> GetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken = default)
    {
        if (_characteristics.TryGetValue((service, characteristic), out var cached))
        {
            return cached;
        }

        var found = await _js.InvokeAsync<bool>(
            "amiitag.bluetooth.getCharacteristic", cancellationToken, Device.Id, WebBluetoothAdapter.ToJs(service), WebBluetoothAdapter.ToJs(characteristic));
        if (!found)
        {
            return null;
        }

        var wrapper = new WebBluetoothCharacteristic(_js, Device.Id, service, characteristic);
        _characteristics[(service, characteristic)] = wrapper;
        return wrapper;
    }

    /// <summary>
    /// Browsers negotiate the MTU themselves.
    /// </summary>
    public Task<int> RequestMtuAsync(int mtu, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public async Task DisconnectAsync()
    {
        try
        {
            foreach (var characteristic in _characteristics.Values)
            {
                characteristic.Dispose();
            }

            await _js.InvokeVoidAsync("amiitag.bluetooth.disconnect", Device.Id);
        }
        catch (JSException)
        {
        }
        finally
        {
            OnDisconnected();
            Reference.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

internal sealed class WebBluetoothCharacteristic : IBleCharacteristic, IDisposable
{
    private readonly IJSRuntime _js;
    private readonly string _deviceId;
    private readonly string _service;
    private readonly string _characteristic;
    private DotNetObjectReference<WebBluetoothCharacteristic>? _reference;

    public WebBluetoothCharacteristic(IJSRuntime js, string deviceId, Guid service, Guid characteristic)
    {
        _js = js;
        _deviceId = deviceId;
        _service = WebBluetoothAdapter.ToJs(service);
        _characteristic = WebBluetoothAdapter.ToJs(characteristic);
        Id = characteristic;
    }

    public Guid Id { get; }

    public event EventHandler<byte[]>? ValueUpdated;

    [JSInvokable]
    public void OnValue(byte[] value) => ValueUpdated?.Invoke(this, value);

    public async Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default)
    {
        try
        {
            await _js.InvokeVoidAsync("amiitag.bluetooth.write", cancellationToken, _deviceId, _service, _characteristic, data, withResponse);
        }
        catch (JSException ex)
        {
            throw new BleException(ex.Message, ex);
        }
    }

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _js.InvokeAsync<byte[]>("amiitag.bluetooth.read", cancellationToken, _deviceId, _service, _characteristic);
        }
        catch (JSException ex)
        {
            throw new BleException(ex.Message, ex);
        }
    }

    public async Task StartNotificationsAsync(CancellationToken cancellationToken = default)
    {
        _reference ??= DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("amiitag.bluetooth.startNotifications", cancellationToken, _deviceId, _service, _characteristic, _reference);
    }

    public async Task StopNotificationsAsync(CancellationToken cancellationToken = default) =>
        await _js.InvokeVoidAsync("amiitag.bluetooth.stopNotifications", cancellationToken, _deviceId, _service, _characteristic);

    public void Dispose()
    {
        _reference?.Dispose();
        _reference = null;
    }
}
