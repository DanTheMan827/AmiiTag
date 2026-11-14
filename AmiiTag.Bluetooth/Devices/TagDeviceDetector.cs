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

        // V2 AmiiboLinks (and a Pixl.js emulating one) advertise the UART UUID with 0000180F as its first group.
        if (device.AdvertisedServices.Contains(AmiiboLinkDevice.V2AdvertisedService))
        {
            return TagDeviceKind.AmiiboLink;
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

        if (name.StartsWith("Pixl", StringComparison.OrdinalIgnoreCase))
        {
            return TagDeviceKind.Pixl;
        }

        return TagDeviceKind.Unknown;
    }

    public static string Describe(TagDeviceKind kind) => kind switch
    {
        TagDeviceKind.Puck => "Puck",
        TagDeviceKind.LegacyPuck => "Puck (legacy firmware)",
        TagDeviceKind.AmiiboLink => "AmiiboLink",
        TagDeviceKind.AmiLoop => "AmiLoop",
        TagDeviceKind.Pixl => "Pixl.js",
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
        throw new BleException($"{device.DisplayName} didn't respond like a puck, Pixl.js, AmiiboLink or AmiLoop.");
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

        // A puck (Espruino) prints its banner as soon as the console moves to Bluetooth, so listen before sending
        // anything: that tells it apart from the other Nordic UART devices without typing into anyone's console.
        status?.Report("Identifying device...");
        if (await HeardEspruinoBannerAsync(connection, cancellationToken))
        {
            status?.Report("Setting up puck...");
            var banner = new PuckDevice(connection, _amiitoolKeys);
            if (await banner.InitializeAsync(cancellationToken))
            {
                return banner;
            }

            banner.Detach();
            throw new BleException($"{connection.Device.DisplayName} runs Espruino but didn't respond like the puck firmware.");
        }

        // Pixl.js before AmiLoop: the AmiLoop handshake starts with 02, which is the Pixl.js's "enter DFU" command,
        // whereas the Pixl.js version request (01 00 00 00) is ignored by the others.
        status?.Report("Checking for Pixl.js...");
        var pixl = new PixlDevice(connection);
        if (await pixl.InitializeAsync(cancellationToken))
        {
            return pixl;
        }

        pixl.Detach();

        status?.Report("Checking for AmiLoop...");
        var amiloop = new AmiLoopDevice(connection);
        if (await amiloop.InitializeAsync(cancellationToken))
        {
            return amiloop;
        }

        amiloop.Detach();

        // AmiiboLink after AmiLoop: the AmiLoop's handshake reply is checked strictly, whereas any answer to the
        // AmiiboLink's info command counts (its format isn't known).
        status?.Report("Checking for AmiiboLink...");
        var amiibolink = new AmiiboLinkDevice(connection);
        if (await amiibolink.ProbeAsync(AmiiboLinkProbeTimeout, cancellationToken))
        {
            return amiibolink;
        }

        amiibolink.Detach();

        // A puck whose banner was missed (it can arrive before notifications are on): ask it directly, last, so the
        // text isn't sent to devices that would misread it.
        status?.Report("Checking for puck firmware...");
        var puck = new PuckDevice(connection, _amiitoolKeys) { FastModeTimeout = TimeSpan.FromSeconds(4), ClearInputLine = true };
        if (await puck.InitializeAsync(cancellationToken))
        {
            return puck;
        }

        puck.Detach();
        return null;
    }

    private TagDeviceBase Create(IBleConnection connection, TagDeviceKind kind) => kind switch
    {
        TagDeviceKind.Puck => new PuckDevice(connection, _amiitoolKeys),
        TagDeviceKind.LegacyPuck => new LegacyPuckDevice(connection),
        TagDeviceKind.AmiiboLink => new AmiiboLinkDevice(connection),
        TagDeviceKind.AmiLoop => new AmiLoopDevice(connection),
        TagDeviceKind.Pixl => new PixlDevice(connection),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// How long to listen for the Espruino banner after connecting.
    /// </summary>
    public TimeSpan BannerTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long to wait for an AmiiboLink to answer its info command.
    /// </summary>
    public TimeSpan AmiiboLinkProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Listens on the UART for the text Espruino sends when a central connects (its banner, which names espruino.com).
    /// </summary>
    private async Task<bool> HeardEspruinoBannerAsync(IBleConnection connection, CancellationToken cancellationToken)
    {
        IBleCharacteristic? notify;
        try
        {
            notify = await connection.GetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartNotify, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (notify == null)
        {
            return false;
        }

        using var queue = new BleNotificationQueue(notify);
        await notify.StartNotificationsAsync(cancellationToken);
        try
        {
            await queue.WaitForTextAsync("spruino", BannerTimeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

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
