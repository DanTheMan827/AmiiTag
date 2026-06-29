namespace AmiiTag.Core.Interfaces
{
    public interface IAppPaths
    {
        /// <summary>
        /// Where user data is stored.
        /// </summary>
        public string UserData { get; }

        /// <summary>
        /// The path to the key file.
        /// </summary>
        public string KeyFile { get; }

        /// <summary>
        /// The base directory for synced databases.
        /// </summary>
        public string DatabaseBase { get; }

        /// <summary>
        /// The path to store scanned NTAG215 signatures.
        /// </summary>
        public string Signatures { get; }

        /// <summary>
        /// Path to the settings database.
        /// </summary>
        public string Settings { get; }

        /// <summary>
        /// The base address.
        /// </summary>
        public string BaseAddress { get; }
    }
}
