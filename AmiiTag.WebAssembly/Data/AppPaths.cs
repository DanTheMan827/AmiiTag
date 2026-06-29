using AmiiTag.Core.Interfaces;

namespace AmiiTag.WebAssembly.Data
{
    public class AppPaths : IAppPaths
    {
        public string UserData => "";

        public string KeyFile => "";

        public string DatabaseBase => "";

        public string Signatures => "";

        public string Settings => "";

        public string BaseAddress { get; init; }
    }
}
