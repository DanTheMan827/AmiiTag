using AmiiTag.Nfc.Flipper;

namespace AmiiTag.Maui.Services;

/// <summary>
/// Several ways to reach the Flipper, best first (the bulk interface where WinUSB has it, then HID).
/// </summary>
public sealed class CompositeFlipperTransportProvider : IFlipperTransportProvider
{
    private readonly IReadOnlyList<IFlipperTransportProvider> _providers;

    public CompositeFlipperTransportProvider(params IFlipperTransportProvider[] providers)
    {
        _providers = providers;
        foreach (var provider in providers)
        {
            provider.DevicesChanged += (_, _) => DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsSupported => _providers.Any(p => p.IsSupported);

    public bool IsDevicePresent => _providers.Any(p => p.IsSupported && p.IsDevicePresent);

    public event EventHandler? DevicesChanged;

    public async Task<IBridgeTransport?> OpenAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers.Where(p => p.IsSupported && p.IsDevicePresent))
        {
            if (await provider.OpenAsync(cancellationToken) is { } transport)
            {
                return transport;
            }
        }

        return null;
    }
}
