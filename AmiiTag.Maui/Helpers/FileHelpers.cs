#if IOS || MACCATALYST
using Foundation;
#endif

namespace AmiiTag.Helpers
{
    internal class FileHelpers
    {
        public static void SetExcludedFromBackup(string filePath, bool value = true)
        {
#if IOS || MACCATALYST
            NSFileManager defaultManager = NSFileManager.DefaultManager;
            NSError error = null;

            NSUrl url = NSUrl.FromFilename(filePath);
            url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(value), out error);

            if (error != null)
            {
                throw new Exception(error.LocalizedDescription);
            }
#endif
        }
    }
}
