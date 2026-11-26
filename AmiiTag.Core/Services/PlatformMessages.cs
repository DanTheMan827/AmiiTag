using AmiiTag.Core.Data;

namespace AmiiTag.Core.Services;

/// <summary>
/// What to tell the user when something isn't available, worded for the version they're running: each message only
/// talks about this platform (no "on Windows..." in the Android app).
/// </summary>
public sealed class PlatformMessages
{
    /// <summary>
    /// Beacio on the App Store: a free app whose Safari extension adds Web Bluetooth to Safari on iPhone and iPad.
    /// </summary>
    public const string BeacioUrl = "https://apps.apple.com/us/app/beacio/id6761301368";

    private readonly AppEnvironment _environment;

    public PlatformMessages(AppEnvironment? environment = null)
    {
        _environment = environment ?? new AppEnvironment();
    }

    public AppEnvironment.PlatformType Platform => _environment.Platform;

    /// <summary>
    /// The web version in Safari on an iPhone or iPad, with no Web Bluetooth detected: Beacio (see <see cref="BeacioUrl"/>)
    /// adds it. The main menu then still shows its Bluetooth button, which explains this instead of opening a picker.
    /// </summary>
    public bool SuggestsBeacio => _environment.Platform == AppEnvironment.PlatformType.WebAssembly && _environment.IsMobileSafari &&
        !_environment.HasWebBluetooth;

    /// <summary>
    /// Why NFC tags can't be read or written here, and what to do about it.
    /// </summary>
    public string NoNfc => _environment.Platform switch
    {
        AppEnvironment.PlatformType.Android => "This device doesn't have NFC, so it can't read or write tags.",
        AppEnvironment.PlatformType.iOS => "This device can't read or write NFC tags. NFC tag reading needs an iPhone 7 or later.",
        AppEnvironment.PlatformType.MacCatalyst => "Reading and writing NFC tags isn't supported on Mac.",
        AppEnvironment.PlatformType.Windows =>
            "No NFC reader is available. Connect a USB NFC reader (such as an ACR122U), or an MFRC522 or PN532 reader on a USB serial adapter.",
        // Only shown when the browser has no Web Serial (navigator.serial), whichever browser it is.
        AppEnvironment.PlatformType.WebAssembly =>
            "This browser doesn't support Web Serial, which AmiiTag uses to talk to an MFRC522 or PN532 NFC reader on a " +
            "USB serial adapter. Open AmiiTag in a browser that supports Web Serial to read and write tags.",
        _ => "This version of AmiiTag can't read or write NFC tags.",
    };

    /// <summary>
    /// Why Bluetooth devices can't be used here.
    /// </summary>
    public string NoBluetooth => _environment.Platform switch
    {
        AppEnvironment.PlatformType.Android => "Bluetooth is off or unavailable. Turn on Bluetooth, and allow AmiiTag to find nearby devices.",
        AppEnvironment.PlatformType.iOS => "Bluetooth is off or unavailable. Turn on Bluetooth, and allow AmiiTag to use it in Settings.",
        AppEnvironment.PlatformType.MacCatalyst => "Bluetooth is off or unavailable. Turn on Bluetooth in System Settings, and allow AmiiTag to use it.",
        AppEnvironment.PlatformType.Windows => "Bluetooth is off or unavailable. Turn on Bluetooth in Settings, or connect a Bluetooth adapter.",
        AppEnvironment.PlatformType.WebAssembly when SuggestsBeacio =>
            "Safari doesn't natively have Web Bluetooth. You can download the free Beacio app from the App Store to add " +
            "support through a Safari extension.",
        AppEnvironment.PlatformType.WebAssembly when !_environment.HasWebBluetooth =>
            "This browser doesn't support Web Bluetooth. Open AmiiTag in a browser that supports Web Bluetooth to use Bluetooth devices.",
        // Web Bluetooth is there, but the browser reports no adapter (getAvailability).
        AppEnvironment.PlatformType.WebAssembly => "Bluetooth is turned off or unavailable on this device.",
        _ => "Bluetooth isn't available.",
    };
}
