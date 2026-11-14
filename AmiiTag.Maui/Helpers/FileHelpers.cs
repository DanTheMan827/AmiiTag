#if IOS || MACCATALYST
using Foundation;
#endif

namespace AmiiTag.Helpers
{
    internal class FileHelpers
    {
        /// <summary>
        /// Marks a file or folder (and so everything in it) as not needing to be backed up.
        /// iOS / Mac Catalyst: NSURLIsExcludedFromBackupKey, so it stays out of iCloud and device backups while
        /// remaining visible in the Files app. Android: the Database folder is excluded by
        /// Resources/xml/backup_rules.xml and data_extraction_rules.xml instead, as Android has no per-file flag.
        /// </summary>
        /// <returns>False if the flag couldn't be set (never fatal: the data is only re-downloadable).</returns>
        public static bool SetExcludedFromBackup(string path, bool value = true)
        {
#if IOS || MACCATALYST
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    return false;
                }

                using var url = NSUrl.FromFilename(path);
                return url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(value), out _);
            }
            catch
            {
                return false;
            }
#else
            return true;
#endif
        }
    }
}
