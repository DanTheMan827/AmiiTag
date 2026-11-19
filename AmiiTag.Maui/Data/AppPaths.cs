using AmiiTag.Core.Interfaces;
using AmiiTag.Helpers;

namespace AmiiTag.Maui.Data
{
    internal class AppPaths : IAppPaths
    {
        public string UserData { get; private set; }
        public string KeyFile { get; private set; }
        public string DatabaseBase { get; private set; }
        public string Signatures { get; private set; }
        public string Settings { get; private set; }
        public string BaseAddress { get; set; } = "/";

        internal AppPaths()
        {
#if IOS || MACCATALYST
            UserData = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
            UserData = FileSystem.AppDataDirectory;
#endif

#if WINDOWS
            MoveOldWindowsData(UserData);
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

#if WINDOWS
        /// <summary>
        /// The unpackaged Windows app keeps its data in %LOCALAPPDATA%\{publisher}\{application ID}\Data. Before the
        /// app had its own identity that was "User Name\com.companyname.amiitag.maui" (the template's); its data (keys,
        /// lock-ons, settings, the database) is copied over the first time, if the new folder doesn't have any yet.
        /// </summary>
        private static void MoveOldWindowsData(string userData)
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var relative = Path.GetRelativePath(localAppData, userData).Split(Path.DirectorySeparatorChar);
                if (relative.Length < 2 || relative[0] == "..")
                {
                    return;
                }

                relative[0] = "User Name";
                relative[1] = "com.companyname.amiitag.maui";
                var old = Path.Combine([localAppData, .. relative]);
                if (!Directory.Exists(old) || string.Equals(Path.GetFullPath(old), Path.GetFullPath(userData), StringComparison.OrdinalIgnoreCase) ||
                    (Directory.Exists(userData) && Directory.EnumerateFileSystemEntries(userData).Any()))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(userData, Path.GetRelativePath(old, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: false);
                }

                System.Diagnostics.Debug.WriteLine($"Copied the app's data from {old} to {userData}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Couldn't copy the old app data: {ex.Message}");
            }
        }
#endif
    }
}
