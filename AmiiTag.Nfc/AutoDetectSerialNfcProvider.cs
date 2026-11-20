namespace AmiiTag.Nfc;

/// <summary>
/// A serial NFC reader whose chip is worked out on connection:
/// <list type="number">
/// <item>the second provider (the PN532) is probed first, when it can be (<see cref="INfcSerialProbe"/>: a PN532
/// command at its power-on 115200 baud that must be answered within 100 ms);</item>
/// <item>no answer means the reader is taken to be the first kind (the MFRC522), which goes through its own setup,
/// cycling baud rates.</item>
/// </list>
/// The kind found is remembered per port for the rest of the session, so only the first connection pays for the detection.
/// </summary>
public sealed class AutoDetectSerialNfcProvider : INfcProvider, INfcReaderRequester, INfcReleasable, INfcBaudRateSetting
{
    private readonly INfcProvider _primary;
    private readonly INfcProvider _fallback;
    private readonly Dictionary<string, INfcProvider> _detected = [];
    private readonly object _sync = new();

    /// <param name="primary">Assumed when the probe gets no answer (the MFRC522, which tries its register interface at every baud rate).</param>
    /// <param name="fallback">Probed first (the PN532).</param>
    public AutoDetectSerialNfcProvider(INfcProvider primary, INfcProvider fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string Id => "serial-auto";

    public string DisplayName => "Serial reader (MFRC522 or PN532)";

    public bool IsSupported => _primary.IsSupported;

    public bool ShowsSystemPrompt => false;

    public bool SupportsReaderSelection => true;

    public bool CanRequestReader => _primary is INfcReaderRequester { CanRequestReader: true };

    // The link speed both readers are switched to once found (each from its own power-on rate), so Settings offers it here.
    public int BaudRate
    {
        get => (_primary as INfcBaudRateSetting)?.BaudRate ?? (_fallback as INfcBaudRateSetting)?.BaudRate ?? 0;
        set
        {
            foreach (var serial in new[] { _primary, _fallback }.OfType<INfcBaudRateSetting>())
            {
                serial.BaudRate = value;
            }
        }
    }

    public int DefaultBaudRate => (_primary as INfcBaudRateSetting)?.DefaultBaudRate ?? 0;

    public IReadOnlyList<int> SupportedBaudRates => (_primary as INfcBaudRateSetting)?.SupportedBaudRates ?? [];

    /// <summary>
    /// The kind of reader found on a port, if it's been connected to.
    /// </summary>
    public INfcProvider? DetectedOn(string portId)
    {
        lock (_sync)
        {
            return _detected.TryGetValue(portId, out var provider) ? provider : null;
        }
    }

    public Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default) =>
        _primary.GetReadersAsync(cancellationToken);

    public Task<NfcReaderInfo?> RequestReaderAsync(CancellationToken cancellationToken = default) =>
        _primary is INfcReaderRequester requester ? requester.RequestReaderAsync(cancellationToken) : Task.FromResult<NfcReaderInfo?>(null);

    public async Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default)
    {
        var portId = await ResolvePortAsync(options, cancellationToken);

        // The port is known (and, in a browser, granted) from here on: no more pickers.
        var portOptions = new NfcConnectOptions { ReaderId = portId, Prompt = options.Prompt, AllowUserPrompts = false };
        if (DetectedOn(portId) is { } detected)
        {
            try
            {
                return await detected.ConnectAsync(portOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is NfcReaderNotFoundException or NfcConnectionLostException)
            {
                // A different reader on the port now: detect again.
                Forget(portId);
                await ReleaseQuietlyAsync(detected);
            }
        }

        // 1. The quick PN532 probe.
        if (_fallback is INfcSerialProbe probe)
        {
            bool answered;
            try
            {
                answered = await probe.ProbeAsync(portId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Serial NFC: {_fallback.DisplayName} probe on {portId} failed: {ex.Message}");
                answered = false;
            }

            if (answered)
            {
                Remember(portId, _fallback);
                return await _fallback.ConnectAsync(portOptions, cancellationToken);
            }

            System.Diagnostics.Debug.WriteLine($"Serial NFC: no {_fallback.DisplayName} answered on {portId}; assuming {_primary.DisplayName}.");
        }

        // 2. No answer: an MFRC522, through its full setup.
        try
        {
            var connection = await _primary.ConnectAsync(portOptions, cancellationToken);
            Remember(portId, _primary);
            return connection;
        }
        catch (NfcReaderNotFoundException ex)
        {
            await ReleaseQuietlyAsync(_primary);
            throw new NfcException(
                $"No NFC reader answered on {portId}: it didn't respond as a PN532 or as an MFRC522 (at any baud rate). " +
                "Check the reader is plugged in, a PN532 is set to HSU (UART) mode, and nothing else is using the port.", ex);
        }
    }

    // The chosen port, else the first one, else (where allowed) the system's port picker.
    private async Task<string> ResolvePortAsync(NfcConnectOptions options, CancellationToken cancellationToken)
    {
        var ports = await GetReadersAsync(cancellationToken);
        var portId = ports.FirstOrDefault(p => p.Id == options.ReaderId)?.Id ?? ports.FirstOrDefault()?.Id;
        if (portId == null && options.AllowUserPrompts && CanRequestReader)
        {
            portId = (await RequestReaderAsync(cancellationToken))?.Id ?? throw new OperationCanceledException();
        }

        return portId ?? throw new NfcException("No serial ports were found. Plug in the NFC reader and try again.");
    }

    private void Remember(string portId, INfcProvider provider)
    {
        lock (_sync)
        {
            _detected[portId] = provider;
        }
    }

    private void Forget(string portId)
    {
        lock (_sync)
        {
            _detected.Remove(portId);
        }
    }

    public async Task ReleaseAsync()
    {
        await ReleaseQuietlyAsync(_primary);
        await ReleaseQuietlyAsync(_fallback);
    }

    private static async Task ReleaseQuietlyAsync(INfcProvider provider)
    {
        if (provider is INfcReleasable releasable)
        {
            try
            {
                await releasable.ReleaseAsync();
            }
            catch
            {
            }
        }
    }
}
