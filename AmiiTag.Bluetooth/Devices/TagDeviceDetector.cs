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
        if (name.Equals("amiibolink", StringComparison.OrdinalIgnoreCase))
        {
            return TagDeviceKind.AmiiboLink;
        }

        if (name.Equals("AmiLoop", StringComparison.OrdinalIgnoreCase))
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
        TagDeviceKind.Flask => "Flask",
        TagDeviceKind.Slide => "Slide",
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
        // Debug builds log the device's BLE traffic.
        var connection = BleTrace.Wrap(await adapter.ConnectAsync(device, cancellationToken));
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
        throw new BleException($"{device.DisplayName} didn't respond like a puck, Flask, Slide, Pixl.js, AmiiboLink or AmiLoop.");
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

        // The devices with binary protocols first, each probe answered only by its own kind; the Espruino ones (puck,
        // Flask, Slide) last, through their JavaScript console.
        // Pixl.js before AmiLoop: the AmiLoop handshake starts with 02, which is the Pixl.js's "enter DFU" command,
        // whereas the Pixl.js version request (01 00 00 00) is ignored by the others.
        status?.Report("Checking for Pixl.js...");
        var pixl = new PixlDevice(connection) { ProbeTimeout = PixlProbeTimeout };
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

        status?.Report("Checking for Espruino...");
        return await DetectEspruinoAsync(connection, status, cancellationToken);
    }

    /// <summary>
    /// Asks the Espruino console which app it runs, without calling into it: the Flask app defines <c>tag.getList</c>
    /// (a Flask or a Slide, told apart by <c>screen.getVersion()</c>), the puck firmware (modern protocol) defines
    /// <c>fastMode</c>. Null if the device doesn't answer like an Espruino console.
    /// </summary>
    private async Task<TagDeviceBase?> DetectEspruinoAsync(IBleConnection connection, IProgress<string>? status, CancellationToken cancellationToken)
    {
        bool flask, puck, slide = false;
        using (var repl = await EspruinoRepl.OpenAsync(connection, cancellationToken))
        {
            if (repl == null)
            {
                return null;
            }

            try
            {
                // Clears what the other probes left on the input line.
                await repl.ClearInputLineAsync(cancellationToken);
                var found = await repl.EvaluateAsync(
                    "{flask:typeof tag!==\"undefined\"&&typeof tag.getList===\"function\",puck:typeof fastMode===\"function\"}",
                    EspruinoProbeTimeout, cancellationToken);
                if (found is not { ValueKind: System.Text.Json.JsonValueKind.Object } apps)
                {
                    return null;
                }

                flask = apps.TryGetProperty("flask", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True;
                puck = apps.TryGetProperty("puck", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.True;
                if (flask && await repl.EvaluateAsync("screen.getVersion()", EspruinoProbeTimeout, cancellationToken) is { ValueKind: System.Text.Json.JsonValueKind.Object } version)
                {
                    // A Slide reports d:"1".
                    slide = version.TryGetProperty("d", out var d) && d.ToString() == "1";
                }
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        if (flask)
        {
            var kind = slide ? TagDeviceKind.Slide : TagDeviceKind.Flask;
            status?.Report($"Setting up {Describe(kind)}...");
            var device = new FlaskDevice(connection, kind);
            if (await device.InitializeAsync(cancellationToken))
            {
                return device;
            }

            device.Detach();
            throw new BleException($"{connection.Device.DisplayName} runs the Flask app but didn't respond to it.");
        }

        if (puck)
        {
            status?.Report("Setting up puck...");
            var device = new PuckDevice(connection, _amiitoolKeys);
            if (await device.InitializeAsync(cancellationToken))
            {
                return device;
            }

            device.Detach();
            throw new BleException($"{connection.Device.DisplayName} runs Espruino but didn't respond like the puck firmware.");
        }

        throw new BleException($"{connection.Device.DisplayName} runs Espruino, but neither the puck firmware nor the Flask app.");
    }

    private TagDeviceBase Create(IBleConnection connection, TagDeviceKind kind) => kind switch
    {
        TagDeviceKind.Puck => new PuckDevice(connection, _amiitoolKeys),
        TagDeviceKind.LegacyPuck => new LegacyPuckDevice(connection),
        TagDeviceKind.AmiiboLink => new AmiiboLinkDevice(connection),
        TagDeviceKind.AmiLoop => new AmiLoopDevice(connection),
        TagDeviceKind.Pixl => new PixlDevice(connection),
        TagDeviceKind.Flask or TagDeviceKind.Slide => new FlaskDevice(connection, kind),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// How long to wait for a Pixl.js to answer its version request while probing an unknown device. A Pixl.js answers
    /// within a connection interval or two; anything else never does, so a short wait keeps detecting the others quick.
    /// </summary>
    public TimeSpan PixlProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long to wait for the Espruino console to answer during detection.
    /// </summary>
    public TimeSpan EspruinoProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long to wait for an AmiiboLink to answer its info command.
    /// </summary>
    public TimeSpan AmiiboLinkProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

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
