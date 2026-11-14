using AmiiTag.Bluetooth.Devices;

namespace AmiiTag.Bluetooth;

/// <summary>
/// App wide list of nearby/known tag devices and the ones currently connected.
/// </summary>
public sealed class BleDeviceManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, BleDeviceInfo> _discovered = new();
    private readonly Dictionary<string, ITagDevice> _connected = new();
    private readonly Dictionary<string, DateTime> _lastSeen = new();
    private CancellationTokenSource? _scanCts;

    public BleDeviceManager(IBleAdapter adapter, TagDeviceDetector detector)
    {
        Adapter = adapter;
        Detector = detector;
        Adapter.DeviceDiscovered += OnDeviceDiscovered;
    }

    public IBleAdapter Adapter { get; }

    public TagDeviceDetector Detector { get; }

    public BleScanFilter Filter { get; set; } = BleUuids.TagDeviceFilter;

    /// <summary>
    /// When true, scanning lists every device instead of only likely tag devices.
    /// </summary>
    public bool ShowAllDevices { get; set; }

    public bool IsScanning => _scanCts != null;

    /// <summary>
    /// While scanning, a device that hasn't advertised for this long is taken off the list (unless it's connected).
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Raised (possibly on a background thread) whenever the lists change.
    /// </summary>
    public event EventHandler? Changed;

    public IReadOnlyList<BleDeviceInfo> Discovered
    {
        get
        {
            lock (_sync)
            {
                return _discovered.Values
                    .OrderByDescending(d => _connected.ContainsKey(d.Id))
                    .ThenByDescending(d => TagDeviceDetector.GuessKind(d) != TagDeviceKind.Unknown || Filter.Matches(d))
                    .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<ITagDevice> Connected
    {
        get
        {
            lock (_sync)
            {
                return _connected.Values.ToList();
            }
        }
    }

    public ITagDevice? GetConnected(string id)
    {
        lock (_sync)
        {
            return _connected.GetValueOrDefault(id);
        }
    }

    public BleDeviceInfo? GetDiscovered(string id)
    {
        lock (_sync)
        {
            return _discovered.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Starts a background scan (on platforms that can scan) and loads previously granted devices.
    /// </summary>
    public async Task<string?> StartScanningAsync()
    {
        var problem = await Adapter.EnsureReadyAsync();
        if (problem != null)
        {
            return problem;
        }

        foreach (var known in await Adapter.GetKnownDevicesAsync())
        {
            AddDiscovered(known);
        }

        if (!Adapter.CanScan || _scanCts != null)
        {
            return null;
        }

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        Changed?.Invoke(this, EventArgs.Empty);

        _ = Task.Run(async () =>
        {
            try
            {
                // Scan without a service filter: AmiiboLink/AmiLoop don't always advertise the UART service.
                await Adapter.StartScanAsync(new BleScanFilter(), cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
            finally
            {
                if (_scanCts == cts)
                {
                    _scanCts = null;
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
        });

        _ = PruneWhileScanningAsync(cts.Token);
        return null;
    }

    /// <summary>
    /// Drops devices that stopped advertising. Only while scanning: without a scan (browsers) nothing says whether a
    /// device is still around, so the granted devices stay listed.
    /// </summary>
    private async Task PruneWhileScanningAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                if (PruneStale(DateTime.UtcNow))
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Removes devices not seen since <see cref="StaleAfter"/> before <paramref name="now"/>. Returns true if any went.
    /// </summary>
    public bool PruneStale(DateTime now)
    {
        lock (_sync)
        {
            var stale = _discovered.Keys
                .Where(id => !_connected.ContainsKey(id) && (!_lastSeen.TryGetValue(id, out var seen) || now - seen > StaleAfter))
                .ToList();
            foreach (var id in stale)
            {
                _discovered.Remove(id);
                _lastSeen.Remove(id);
            }

            return stale.Count > 0;
        }
    }

    public async Task StopScanningAsync()
    {
        var cts = _scanCts;
        _scanCts = null;
        if (cts != null)
        {
            cts.Cancel();
            try
            {
                await Adapter.StopScanAsync();
            }
            catch
            {
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the platform device picker (browsers). Must be called from a user gesture.
    /// </summary>
    public async Task<BleDeviceInfo?> RequestDeviceAsync(CancellationToken cancellationToken = default)
    {
        var device = await Adapter.RequestDeviceAsync(Filter, cancellationToken);
        if (device != null)
        {
            AddDiscovered(device);
        }

        return device;
    }

    /// <summary>
    /// Returns the existing connection to the device, or connects and identifies it.
    /// </summary>
    public async Task<ITagDevice> ConnectAsync(BleDeviceInfo device, TagDeviceKind? forcedKind = null, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        // Use the latest advertisement's details: on iOS the platform object from an earlier scan can be stale.
        device = GetDiscovered(device.Id) ?? device;

        var existing = GetConnected(device.Id);
        if (existing is { IsConnected: true } && (forcedKind == null || existing.Kind == forcedKind))
        {
            return existing;
        }

        if (existing != null)
        {
            await DisconnectAsync(existing);
        }

        var driver = await Detector.ConnectAsync(Adapter, device, forcedKind, status, cancellationToken);
        driver.Disconnected += OnDriverDisconnected;
        lock (_sync)
        {
            _connected[device.Id] = driver;
            _discovered[device.Id] = device;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return driver;
    }

    public async Task DisconnectAsync(ITagDevice device)
    {
        lock (_sync)
        {
            _connected.Remove(device.Info.Id);

            // Give it a moment to show up advertising again before it counts as gone.
            _lastSeen[device.Info.Id] = DateTime.UtcNow;
        }

        device.Disconnected -= OnDriverDisconnected;
        try
        {
            await device.DisconnectAsync();
        }
        catch
        {
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var device in Connected)
        {
            await DisconnectAsync(device);
        }
    }

    private void OnDriverDisconnected(object? sender, EventArgs e)
    {
        if (sender is ITagDevice device)
        {
            device.Disconnected -= OnDriverDisconnected;
            lock (_sync)
            {
                if (_connected.TryGetValue(device.Info.Id, out var current) && ReferenceEquals(current, device))
                {
                    _connected.Remove(device.Info.Id);
                    _lastSeen[device.Info.Id] = DateTime.UtcNow;
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDeviceDiscovered(object? sender, BleDeviceInfo device)
    {
        if (!ShowAllDevices && !Filter.Matches(device) && TagDeviceDetector.GuessKind(device) == TagDeviceKind.Unknown)
        {
            return;
        }

        AddDiscovered(device);
    }

    private void AddDiscovered(BleDeviceInfo device)
    {
        bool changed;
        lock (_sync)
        {
            var known = _discovered.TryGetValue(device.Id, out var existing);

            // Keep the name we already know if a later advertisement packet omits it.
            if (known && string.IsNullOrEmpty(device.Name) && !string.IsNullOrEmpty(existing!.Name))
            {
                device = device with { Name = existing.Name };
            }

            // Advertisements repeat several times a second; only a new device, a new name or a clear change in
            // signal is worth redrawing the list for.
            changed = !known || existing!.Name != device.Name || Math.Abs(existing.Rssi - device.Rssi) >= 6;
            _discovered[device.Id] = changed || !known ? device : existing! with { Native = device.Native ?? existing.Native };
            _lastSeen[device.Id] = DateTime.UtcNow;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
