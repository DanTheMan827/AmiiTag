namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// Works out what kind of device something is: first from what it advertises (services and name),
/// then, for anything on the shared Nordic UART service, by probing how it responds.
/// </summary>
public sealed class TagDeviceDetector
{
    private readonly Func<byte[]?>? _amiitoolKeys;

    /// <param name="amiitoolKeys">Supplies key_retail.bin for pucks that support amiitool.</param>
    public TagDeviceDetector(Func<byte[]?>? amiitoolKeys = null)
    {
        _amiitoolKeys = amiitoolKeys;
    }

    /// <summary>
    /// Guesses the kind from the advertisement alone. <see cref="TagDeviceKind.Unknown"/> means "probe it".
    /// </summary>
    public static TagDeviceKind GuessKind(BleDeviceInfo device)
    {
        if (device.AdvertisedServices.Contains(BleUuids.LegacyPuckService))
        {
            return TagDeviceKind.LegacyPuck;
        }

        var name = device.Name ?? string.Empty;
        if (name.StartsWith("amiibolink", StringComparison.OrdinalIgnoreCase))
        {
            return TagDeviceKind.AmiiboLink;
        }

        if (name.StartsWith("AmiLoop", StringComparison.OrdinalIgnoreCase))
        {
            return TagDeviceKind.AmiLoop;
        }

        return TagDeviceKind.Unknown;
    }

    public static string Describe(TagDeviceKind kind) => kind switch
    {
        TagDeviceKind.Puck => "Puck",
        TagDeviceKind.LegacyPuck => "Puck (legacy firmware)",
        TagDeviceKind.AmiiboLink => "AmiiboLink",
        TagDeviceKind.AmiLoop => "AmiLoop",
        _ => "Unknown device",
    };

    /// <summary>
    /// Connects and returns a ready driver.
    /// </summary>
    /// <param name="adapter">The BLE adapter.</param>
    /// <param name="device">The device to connect to.</param>
    /// <param name="forcedKind">Skip detection and treat the device as this kind.</param>
    /// <param name="status">Receives progress text.</param>
    public async Task<TagDeviceBase> ConnectAsync(
        IBleAdapter adapter,
        BleDeviceInfo device,
        TagDeviceKind? forcedKind = null,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        status?.Report($"Connecting to {device.DisplayName}...");
        var connection = await adapter.ConnectAsync(device, cancellationToken);
        try
        {
            var driver = await DetectAsync(connection, forcedKind ?? GuessKind(device), status, cancellationToken);
            if (driver != null)
            {
                return driver;
            }
        }
        catch
        {
            await SafeDisconnectAsync(connection);
            throw;
        }

        await SafeDisconnectAsync(connection);
        throw new BleException($"{device.DisplayName} didn't respond like a puck, AmiiboLink or AmiLoop.");
    }

    private async Task<TagDeviceBase?> DetectAsync(IBleConnection connection, TagDeviceKind hint, IProgress<string>? status, CancellationToken cancellationToken)
    {
        if (hint != TagDeviceKind.Unknown)
        {
            status?.Report($"Setting up {Describe(hint)}...");
            var driver = Create(connection, hint);
            if (await driver.InitializeAsync(cancellationToken))
            {
                return driver;
            }

            driver.Detach();
            throw new BleException($"{connection.Device.DisplayName} looks like an {Describe(hint)} but didn't respond like one.");
        }

        // Browsers don't expose advertised services, so check what the connected device actually has.
        if (await connection.HasServiceAsync(BleUuids.LegacyPuckService, cancellationToken))
        {
            var legacy = new LegacyPuckDevice(connection);
            if (await legacy.InitializeAsync(cancellationToken))
            {
                return legacy;
            }

            legacy.Detach();
        }

        if (!await connection.HasServiceAsync(BleUuids.NordicUartService, cancellationToken))
        {
            return null;
        }

        // Puck first: "fastMode()\n" is plain text, which the AmiLoop/AmiiboLink framing ignores,
        // whereas the AmiLoop handshake would be typed into the puck's JavaScript console.
        status?.Report("Checking for puck firmware...");
        var puck = new PuckDevice(connection, _amiitoolKeys) { FastModeTimeout = TimeSpan.FromSeconds(4) };
        if (await puck.InitializeAsync(cancellationToken))
        {
            return puck;
        }

        puck.Detach();

        status?.Report("Checking for AmiLoop...");
        var amiloop = new AmiLoopDevice(connection);
        if (await amiloop.InitializeAsync(cancellationToken))
        {
            return amiloop;
        }

        amiloop.Detach();

        // The AmiiboLink has no handshake to probe; it has to be identified by name or chosen by the user.
        return null;
    }

    private TagDeviceBase Create(IBleConnection connection, TagDeviceKind kind) => kind switch
    {
        TagDeviceKind.Puck => new PuckDevice(connection, _amiitoolKeys),
        TagDeviceKind.LegacyPuck => new LegacyPuckDevice(connection),
        TagDeviceKind.AmiiboLink => new AmiiboLinkDevice(connection),
        TagDeviceKind.AmiLoop => new AmiLoopDevice(connection),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task SafeDisconnectAsync(IBleConnection connection)
    {
        try
        {
            await connection.DisconnectAsync();
        }
        catch
        {
        }
    }
}
