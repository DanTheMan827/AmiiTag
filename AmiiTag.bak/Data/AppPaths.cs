using AmiiTag.Helpers;

namespace AmiiTag.Data
{
    internal class AppPaths
    {
        public string UserData { get; private set; }


        public string KeyFile { get; private set; }
        public string DatabaseBase { get; private set; }
        public string Signatures { get; private set; }
        public string Settings { get; private set; }

        public static Lazy<AppPaths> _shared = new Lazy<AppPaths>(() => new AppPaths());

        public static AppPaths Shared => _shared.Value;

        private AppPaths()
        {
#if IOS || MACCATALYST
            UserData = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
            UserData = FileSystem.AppDataDirectory;
#endif

            KeyFile = Path.Combine(UserData, "key_retail.bin");
            DatabaseBase = Path.Combine(UserData, "Database");
            Signatures = Path.Combine(UserData, "Signatures");
            Settings = Path.Combine(UserData, "Settings.db");

            Directory.CreateDirectory(UserData);
            Directory.CreateDirectory(DatabaseBase);
            Directory.CreateDirectory(Signatures);

            FileHelpers.SetExcludedFromBackup(DatabaseBase);
        }
    }
}
