namespace AmiiTag.Core
{
    public static class AmiiTagEnvironment
    {
        public static IServiceProvider? ServiceProvider { get; set; }
        public static EnvironmentType Type { get; set; }

        public enum EnvironmentType
        {
            Unknown,
            WebAssembly,
            Maui
        }
    }
}
