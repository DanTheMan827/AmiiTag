using AmiiTag.Bluetooth;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using BleAdapterContract = Plugin.BLE.Abstractions.Contracts.IAdapter;
using BleCharacteristicContract = Plugin.BLE.Abstractions.Contracts.ICharacteristic;
using BleDeviceContract = Plugin.BLE.Abstractions.Contracts.IDevice;

namespace AmiiTag.Maui.Services;

/// <summary>
/// <see cref="IBleAdapter"/> on top of Plugin.BLE (Android, iOS, Mac Catalyst, Windows).
/// </summary>
public sealed class PluginBleAdapter : IBleAdapter
{
    private readonly IBluetoothLE _ble = CrossBluetoothLE.Current;
    private readonly Dictionary<Guid, PluginBleConnection> _connections = new();

    public PluginBleAdapter()
    {
        Adapter.DeviceDiscovered += OnDeviceDiscovered;
        Adapter.DeviceAdvertised += OnDeviceDiscovered;
        Adapter.DeviceDisconnected += OnDeviceDisconnected;
        Adapter.DeviceConnectionLost += OnDeviceConnectionLost;
    }

    private BleAdapterContract Adapter => _ble.Adapter;

    public bool IsAvailable => _ble.IsAvailable;

    public bool CanScan => true;

    public bool CanRequestDevice => false;

    public event EventHandler<BleDeviceInfo>? DeviceDiscovered;

    /// <summary>
    /// Plugin.BLE reports Unavailable when the device has no Bluetooth LE hardware.
    /// </summary>
    public Task<bool> CheckAvailabilityAsync(CancellationToken cancellationToken = default) => Task.FromResult(_ble.IsAvailable);

    public async Task<string?> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
#if ANDROID
        var status = await MainThread.InvokeOnMainThreadAsync(() => Permissions.RequestAsync<Permissions.Bluetooth>());
        if (status != PermissionStatus.Granted)
        {
            return "Bluetooth permission was denied. Allow \"Nearby devices\" for AmiiTag in Settings.";
        }
#else
        await Task.CompletedTask;
#endif

        if (!_ble.IsAvailable)
        {
            return "Bluetooth LE isn't available on this device.";
        }

        if (!_ble.IsOn)
        {
            return "Bluetooth is turned off.";
        }

        return null;
    }

    public async Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken = default)
    {
        Adapter.ScanMode = ScanMode.LowLatency;
        Adapter.ScanTimeout = 30000;

        ScanFilterOptions? options = null;
        if (filter.Services.Count > 0)
        {
            options = new ScanFilterOptions { ServiceUuids = filter.Services.ToArray() };
        }

        // Each scan stops after ScanTimeout; keep restarting until cancelled.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Adapter.StartScanningForDevicesAsync(options, null, false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public Task StopScanAsync() => Adapter.IsScanning ? Adapter.StopScanningForDevicesAsync() : Task.CompletedTask;

    public Task<BleDeviceInfo?> RequestDeviceAsync(BleScanFilter filter, CancellationToken cancellationToken = default) =>
        Task.FromResult<BleDeviceInfo?>(null);

    public Task<IReadOnlyList<BleDeviceInfo>> GetKnownDevicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BleDeviceInfo> devices = Adapter.ConnectedDevices.Select(ToInfo).ToList();
        return Task.FromResult(devices);
    }

    public async Task<IBleConnection> ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default)
    {
        if (device.Native is not BleDeviceContract native)
        {
            throw new BleException("This device is no longer available. Scan again.");
        }

        try
        {
            await Adapter.ConnectToDeviceAsync(native, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BleException($"Couldn't connect to {device.DisplayName}: {ex.Message}", ex);
        }

        var connection = new PluginBleConnection(Adapter, native, device);
        lock (_connections)
        {
            _connections[native.Id] = connection;
        }

        return connection;
    }

    private void OnDeviceDiscovered(object? sender, DeviceEventArgs e) => DeviceDiscovered?.Invoke(this, ToInfo(e.Device));

    private void OnDeviceDisconnected(object? sender, DeviceEventArgs e) => MarkDisconnected(e.Device);

    private void OnDeviceConnectionLost(object? sender, DeviceErrorEventArgs e) => MarkDisconnected(e.Device);

    private void MarkDisconnected(BleDeviceContract device)
    {
        PluginBleConnection? connection;
        lock (_connections)
        {
            if (!_connections.Remove(device.Id, out connection))
            {
                return;
            }
        }

        connection.OnDisconnected();
    }

    internal static BleDeviceInfo ToInfo(BleDeviceContract device)
    {
        var services = new List<Guid>();
        foreach (var record in device.AdvertisementRecords ?? Enumerable.Empty<AdvertisementRecord>())
        {
            // 0x06 / 0x07: incomplete / complete list of 128 bit service UUIDs.
            if ((int)record.Type is 0x06 or 0x07 && record.Data != null)
            {
                services.AddRange(BleUuids.ParseUuid128List(record.Data));
            }
        }

        return new BleDeviceInfo(device.Id.ToString(), device.Name, services, device.Rssi) { Native = device };
    }
}

internal sealed class PluginBleConnection : IBleConnection
{
    private readonly BleAdapterContract _adapter;
    private readonly BleDeviceContract _device;
    private readonly Dictionary<(Guid, Guid), PluginBleCharacteristic> _characteristics = new();

    public PluginBleConnection(BleAdapterContract adapter, BleDeviceContract device, BleDeviceInfo info)
    {
        _adapter = adapter;
        _device = device;
        Device = info;
    }

    public BleDeviceInfo Device { get; }

    public bool IsConnected { get; private set; } = true;

    public event EventHandler? Disconnected;

    internal void OnDisconnected()
    {
        if (!IsConnected)
        {
            return;
        }

        IsConnected = false;
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> HasServiceAsync(Guid service, CancellationToken cancellationToken = default) =>
        await _device.GetServiceAsync(service, cancellationToken) != null;

    public async Task<IBleCharacteristic?> GetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken = default)
    {
        lock (_characteristics)
        {
            if (_characteristics.TryGetValue((service, characteristic), out var cached))
            {
                return cached;
            }
        }

        var nativeService = await _device.GetServiceAsync(service, cancellationToken);
        if (nativeService == null)
        {
            return null;
        }

        var nativeCharacteristic = await nativeService.GetCharacteristicAsync(characteristic);
        if (nativeCharacteristic == null)
        {
            return null;
        }

        var wrapper = new PluginBleCharacteristic(nativeCharacteristic);
        lock (_characteristics)
        {
            _characteristics[(service, characteristic)] = wrapper;
        }

        return wrapper;
    }

    public async Task<int> RequestMtuAsync(int mtu, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _device.RequestMtuAsync(mtu);
        }
        catch
        {
            return 0;
        }
    }

    public async Task DisconnectAsync()
    {
        try
        {
            await _adapter.DisconnectDeviceAsync(_device);
        }
        finally
        {
            OnDisconnected();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}

internal sealed class PluginBleCharacteristic : IBleCharacteristic
{
    private readonly BleCharacteristicContract _characteristic;

    public PluginBleCharacteristic(BleCharacteristicContract characteristic)
    {
        _characteristic = characteristic;
        _characteristic.ValueUpdated += OnValueUpdated;
    }

    public Guid Id => _characteristic.Id;

    public event EventHandler<byte[]>? ValueUpdated;

    private void OnValueUpdated(object? sender, CharacteristicUpdatedEventArgs e) =>
        ValueUpdated?.Invoke(this, e.Characteristic.Value ?? Array.Empty<byte>());

    public async Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default)
    {
        _characteristic.WriteType = withResponse ? CharacteristicWriteType.WithResponse : CharacteristicWriteType.WithoutResponse;
        cancellationToken.ThrowIfCancellationRequested();
        await _characteristic.WriteAsync(data);
    }

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Plugin.BLE 3 returns (data, resultCode); reading Value afterwards works with every version.
        cancellationToken.ThrowIfCancellationRequested();
        await _characteristic.ReadAsync();
        return _characteristic.Value ?? Array.Empty<byte>();
    }

    public Task StartNotificationsAsync(CancellationToken cancellationToken = default) => _characteristic.StartUpdatesAsync();

    public Task StopNotificationsAsync(CancellationToken cancellationToken = default) => _characteristic.StopUpdatesAsync();
}
