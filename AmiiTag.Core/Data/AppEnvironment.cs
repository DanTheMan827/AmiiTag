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
    }
}
