namespace AmiiTag.Bluetooth;

/// <summary>
/// The BLE operations the device drivers need, shaped after Plugin.BLE's IAdapter/IDevice/ICharacteristic.
/// Implemented by a Plugin.BLE wrapper in the MAUI app and by WebBluetooth in the browser.
/// </summary>
public interface IBleAdapter
{
    /// <summary>
    /// True if Bluetooth LE exists on this device/browser.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// True if <see cref="StartScanAsync"/> works. Browsers can't scan freely and use <see cref="RequestDeviceAsync"/> instead.
    /// </summary>
    bool CanScan { get; }

    /// <summary>
    /// True if <see cref="RequestDeviceAsync"/> shows a system device picker.
    /// </summary>
    bool CanRequestDevice { get; }

    /// <summary>
    /// Checks for Bluetooth LE hardware/browser support without asking for permissions, and updates <see cref="IsAvailable"/>.
    /// </summary>
    Task<bool> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests permissions and checks Bluetooth is turned on. Returns a reason when it isn't ready.
    /// </summary>
    Task<string?> EnsureReadyAsync(CancellationToken cancellationToken = default);

    event EventHandler<BleDeviceInfo>? DeviceDiscovered;

    /// <summary>
    /// Scans until <paramref name="cancellationToken"/> is cancelled or <see cref="StopScanAsync"/> is called.
    /// </summary>
    Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken = default);

    Task StopScanAsync();

    /// <summary>
    /// Shows the platform's device picker (WebBluetooth's requestDevice).
    /// </summary>
    Task<BleDeviceInfo?> RequestDeviceAsync(BleScanFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devices the user granted access to earlier (WebBluetooth's getDevices), or already connected devices.
    /// </summary>
    Task<IReadOnlyList<BleDeviceInfo>> GetKnownDevicesAsync(CancellationToken cancellationToken = default);

    Task<IBleConnection> ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default);
}

/// <summary>
/// A discovered device.
/// </summary>
/// <param name="Id">A platform specific identifier.</param>
/// <param name="Name">The advertised name.</param>
/// <param name="AdvertisedServices">Service UUIDs from the advertisement, if the platform reports them.</param>
/// <param name="Rssi">Signal strength, or 0 if unknown.</param>
public sealed record BleDeviceInfo(string Id, string? Name, IReadOnlyList<Guid> AdvertisedServices, int Rssi = 0)
{
    /// <summary>
    /// The platform's device object (Plugin.BLE IDevice), if any.
    /// </summary>
    public object? Native { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Unnamed device" : Name!;
}

public sealed class BleScanFilter
{
    public IReadOnlyList<Guid> Services { get; init; } = [];
    public IReadOnlyList<string> Names { get; init; } = [];
    public IReadOnlyList<string> NamePrefixes { get; init; } = [];
    public IReadOnlyList<string> NameContains { get; init; } = [];

    /// <summary>
    /// Services that must be accessible after connecting even though they aren't used to filter (WebBluetooth).
    /// </summary>
    public IReadOnlyList<Guid> OptionalServices { get; init; } = [];

    public bool Matches(BleDeviceInfo device)
    {
        if (device.AdvertisedServices.Any(Services.Contains))
        {
            return true;
        }

        var name = device.Name;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ||
               NamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
               NameContains.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}

public interface IBleConnection : IAsyncDisposable
{
    BleDeviceInfo Device { get; }

    bool IsConnected { get; }

    event EventHandler? Disconnected;

    Task<bool> HasServiceAsync(Guid service, CancellationToken cancellationToken = default);

    Task<IBleCharacteristic?> GetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests a larger MTU. Returns the negotiated MTU, or 0 if the platform doesn't support it.
    /// </summary>
    Task<int> RequestMtuAsync(int mtu, CancellationToken cancellationToken = default);

    /// <summary>
    /// The device is about to drop the link itself (restarting its Bluetooth, say to apply a new name): the platform
    /// mustn't reconnect to it when it comes back. Windows otherwise does, straight away, which keeps the device from
    /// ever finishing its restart.
    /// </summary>
    Task ExpectDeviceDisconnectAsync() => Task.CompletedTask;

    Task DisconnectAsync();
}

public interface IBleCharacteristic
{
    Guid Id { get; }

    Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default);

    Task<byte[]> ReadAsync(CancellationToken cancellationToken = default);

    Task StartNotificationsAsync(CancellationToken cancellationToken = default);

    Task StopNotificationsAsync(CancellationToken cancellationToken = default);

    event EventHandler<byte[]>? ValueUpdated;
}

public class BleException : Exception
{
    public BleException(string message) : base(message)
    {
    }

    public BleException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
