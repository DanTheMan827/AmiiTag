namespace AmiiTag.Core.Data
{
    public class AppEnvironment
    {
        public enum PlatformType
        {
            Unknown,
            iOS,
            Android,
            MacCatalyst,
            Windows,
            WebAssembly
        }

        public PlatformType Platform { get; init; } = PlatformType.Unknown;

        /// <summary>
        /// The web version running in a browser on an iPhone or iPad.
        /// </summary>
        public bool IsIosBrowser { get; init; }

        /// <summary>
        /// The web version running in Safari itself on an iPhone or iPad (not another browser there).
        /// </summary>
        public bool IsMobileSafari { get; init; }

        /// <summary>
        /// The web version: the browser has Web Bluetooth (navigator.bluetooth), natively or added by an extension.
        /// </summary>
        public bool HasWebBluetooth { get; init; }
    }
}
