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

        // Each scan stops after ScanTimeout; keep restarting until cancelled. Duplicates are allowed so every
        // advertisement is reported (DeviceAdvertised): the device list drops devices that stop advertising.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Adapter.StartScanningForDevicesAsync(options, null, true, cancellationToken);
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
        // On iOS a device object stops being usable once a new scan starts (and scans restart every 30 seconds), so
        // prefer the one from the current scan; failing that, connect by identifier, which doesn't depend on a scan
        // (retrievePeripheralsWithIdentifiers on iOS, the address on Android).
        Guid.TryParse(device.Id, out var id);
        var native = Adapter.DiscoveredDevices.FirstOrDefault(d => d.Id == id) ?? device.Native as BleDeviceContract;

        Exception? failure = null;
        if (native != null)
        {
            try
            {
                await Adapter.ConnectToDeviceAsync(native, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ex;
                native = null;
            }
        }

        if (native == null)
        {
            if (id == Guid.Empty)
            {
                throw new BleException($"Couldn't connect to {device.DisplayName}: {failure?.Message ?? "it's no longer available"}.", failure ?? new InvalidOperationException());
            }

            try
            {
                native = await Adapter.ConnectToKnownDeviceAsync(id, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BleException($"Couldn't connect to {device.DisplayName}: {(failure ?? ex).Message}", failure ?? ex);
            }
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

    /// <summary>
    /// The longest each disconnect step waits on the device; one that has restarted or gone may never answer.
    /// </summary>
    private static readonly TimeSpan DisconnectStepTimeout = TimeSpan.FromSeconds(2);

    public async Task DisconnectAsync()
    {
        // Turn notifications off first: Windows keeps the link up while any subscription (or service object) is alive,
        // so a plain disconnect could leave the device connected.
        List<PluginBleCharacteristic> characteristics;
        lock (_characteristics)
        {
            characteristics = _characteristics.Values.ToList();
            _characteristics.Clear();
        }

        foreach (var characteristic in characteristics)
        {
            try
            {
                await characteristic.StopNotificationsAsync().WaitAsync(DisconnectStepTimeout);
            }
            catch
            {
                // Never started, or the link is already gone.
            }

            characteristic.Release();
        }

#if WINDOWS
        // Plugin.BLE opens its GATT session with MaintainConnection on, which tells Windows to keep (and re-establish)
        // the link; disposing the session doesn't always undo that. It's switched off first.
        await ExpectDeviceDisconnectAsync();
#endif

        try
        {
            await _adapter.DisconnectDeviceAsync(_device).WaitAsync(DisconnectStepTimeout);
        }
        catch
        {
            // Includes a device that restarted or went out of range: Windows can wait a long time for it.
        }
        finally
        {
#if WINDOWS
            await ReleaseWindowsLinkAsync();
#endif
            OnDisconnected();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

#if WINDOWS
    /// <summary>
    /// Windows: Plugin.BLE's GATT session for the device has MaintainConnection on, so Windows reconnects whenever
    /// the device reappears: after it restarts its Bluetooth (a puck applying a new name), or even after Bluetooth is
    /// turned off and on, and the device never gets to advertise again. MaintainConnection is a request held by each
    /// session object, so it's switched off on the plugin's own object (a private field, found by type).
    /// </summary>
    public Task ExpectDeviceDisconnectAsync()
    {
        var released = 0;
        for (var type = _device.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            var fields = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly);
            foreach (var field in fields.Where(f => typeof(global::Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession).IsAssignableFrom(f.FieldType)))
            {
                try
                {
                    if (field.GetValue(_device) is global::Windows.Devices.Bluetooth.GenericAttributeProfile.GattSession session)
                    {
                        session.MaintainConnection = false;
                        released++;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"BLE {Device.DisplayName}: couldn't release the GATT session ({ex.Message}).");
                }
            }
        }

        System.Diagnostics.Debug.WriteLine($"BLE {Device.DisplayName}: MaintainConnection switched off on {released} GATT session(s).");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Windows has no "disconnect": the link drops once nothing in the app holds the device, its GATT session or any
    /// of its services. Plugin.BLE disposes its session and services; this disposes the device too, then collects the
    /// WinRT wrappers still waiting for the garbage collector (each one keeps the link up until it's finalized).
    /// </summary>
    private async Task ReleaseWindowsLinkAsync()
    {
        var native = _device.NativeDevice as global::Windows.Devices.Bluetooth.BluetoothLEDevice;
        var deviceId = native?.DeviceId;
        try
        {
            _device.Dispose();
        }
        catch
        {
        }

        // Windows drops the link only once every BluetoothLEDevice object the app has for the device is gone, and
        // Plugin.BLE can hold more than one: the one connected (which it may recreate while disconnecting), and the
        // one from the scan, kept in its discovered list while scanning carries on. Each is closed explicitly.
        var others = _adapter.DiscoveredDevices.Concat(_adapter.ConnectedDevices).Where(d => d.Id == _device.Id).Append(_device);
        foreach (var nativeObject in others.Select(d => d.NativeDevice).Append(native).OfType<global::Windows.Devices.Bluetooth.BluetoothLEDevice>().Distinct())
        {
            try
            {
                nativeObject.Dispose();
            }
            catch
            {
            }
        }

        native = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (deviceId != null)
        {
            _ = LogWindowsLinkStateAsync(deviceId);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Windows takes a moment to drop the link; logs what it ends up as. A paired device stays connected, as Windows
    /// itself keeps paired devices connected.
    /// </summary>
    private async Task LogWindowsLinkStateAsync(string deviceId)
    {
        await Task.Delay(1500);
        try
        {
            using var check = await global::Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(deviceId);
            System.Diagnostics.Debug.WriteLine(
                $"BLE disconnect {Device.DisplayName}: Windows reports {check?.ConnectionStatus}, paired: {check?.DeviceInformation?.Pairing?.IsPaired}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BLE disconnect check failed: {ex.Message}");
        }
    }
#endif
}

internal sealed class PluginBleCharacteristic : IBleCharacteristic
{
    private BleCharacteristicContract? _characteristic;

    public PluginBleCharacteristic(BleCharacteristicContract characteristic)
    {
        _characteristic = characteristic;
        _characteristic.ValueUpdated += OnValueUpdated;
        Id = characteristic.Id;
    }

    public Guid Id { get; }

    public event EventHandler<byte[]>? ValueUpdated;

    // Released when the connection closes, so the native object (which keeps a Windows link alive) can go.
    private BleCharacteristicContract Native => _characteristic ?? throw new BleException("The device isn't connected.");

    private void OnValueUpdated(object? sender, CharacteristicUpdatedEventArgs e) =>
        ValueUpdated?.Invoke(this, e.Characteristic.Value ?? Array.Empty<byte>());

    public async Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default)
    {
        var native = Native;
        native.WriteType = withResponse ? CharacteristicWriteType.WithResponse : CharacteristicWriteType.WithoutResponse;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // A device that drops the link before confirming a write can leave Windows waiting a long time.
            await native.WriteAsync(data).WaitAsync(WriteTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The device didn't confirm the write.");
        }
    }

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Plugin.BLE 3 returns (data, resultCode); reading Value afterwards works with every version.
        var native = Native;
        cancellationToken.ThrowIfCancellationRequested();
        await native.ReadAsync();
        return native.Value ?? Array.Empty<byte>();
    }

    public Task StartNotificationsAsync(CancellationToken cancellationToken = default) => Native.StartUpdatesAsync();

    public Task StopNotificationsAsync(CancellationToken cancellationToken = default) =>
        _characteristic?.StopUpdatesAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Stops forwarding values and lets go of the native characteristic (the connection is closing).
    /// </summary>
    internal void Release()
    {
        if (_characteristic != null)
        {
            _characteristic.ValueUpdated -= OnValueUpdated;
            _characteristic = null;
        }
    }
}
