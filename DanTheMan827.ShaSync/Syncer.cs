using System.Security.Cryptography;
using System.Text.Json;

namespace DanTheMan827.ShaSync
{
    /// <summary>
    /// Utility for synchronizing files based on a manifest and SHA-1 hash values.
    /// </summary>
    public class Syncer
    {
        // Event to report synchronization progress
        public event EventHandler<SyncProgressEventArgs> SyncProgress;

        // EventArgs class for the SyncProgress event
        public class SyncProgressEventArgs : EventArgs
        {
            public ManifestItem? SyncItem { get; }
            public int Index { get; }
            public int Total { get; }

            public SyncProgressEventArgs(ManifestItem? syncItem, int index, int total)
            {
                SyncItem = syncItem;
                Index = index;
                Total = total;
            }
        }

        /// <summary>
        /// Gets the base URL for the synchronization.
        /// </summary>
        public Uri BaseUrl { get; private set; }

        /// <summary>
        /// Gets the URL of the manifest used for synchronization.
        /// </summary>
        public Uri ManifestUrl { get; private set; }

        /// <summary>
        /// Gets the local path where files will be synchronized.
        /// </summary>
        public string LocalPath { get; private set; }

        /// <summary>
        /// Represents a manifest item.
        /// </summary>
        public class ManifestItem
        {
            /// <summary>
            /// The destination file path.
            /// </summary>
            public string DestinationFile { get; internal set; }

            /// <summary>
            /// The destination folder of the file.
            /// </summary>
            public string DestinationFolder => Path.GetDirectoryName(DestinationFile);

            /// <summary>
            /// The source URL for the file.
            /// </summary>
            public string SourceUrl { get; internal set; }

            /// <summary>
            /// The path as specified in the manifest.
            /// </summary>
            public string Key { get; internal set; }

            /// <summary>
            /// The SHA-1 hash of the item.
            /// </summary>
            public string Hash { get; internal set; }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Syncer"/> class.
        /// </summary>
        /// <param name="manifestUrl">The URL of the manifest used for synchronization.</param>
        /// <param name="localPath">The local path where files will be synchronized.</param>
        public Syncer(string manifestUrl, string localPath)
        {
            this.BaseUrl = new Uri(manifestUrl.Substring(0, manifestUrl.LastIndexOf("/") + 1));
            this.ManifestUrl = new Uri(BaseUrl, manifestUrl.Substring(manifestUrl.LastIndexOf("/") + 1));
            this.LocalPath = Path.GetFullPath(localPath);
        }

        /// <summary>
        /// Computes the SHA-1 hash of a file.
        /// </summary>
        /// <param name="filePath">The path of the file.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>The computed SHA-1 hash.</returns>
        private static async Task<string> ComputeSHA1Async(string filePath, CancellationToken cancellationToken = default)
        {
            using (var sha1 = SHA1.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hashBytes = await sha1.ComputeHashAsync(stream, cancellationToken);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
                }
            }
        }

        /// <summary>
        /// Gets a list of items to be synchronized based on a manifest.
        /// </summary>
        /// <param name="manifest">The manifest dictionary.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>An array of SyncItems to be synchronized.</returns>
        private async Task<ManifestItem[]> GetSyncItemsAsync(IEnumerable<ManifestItem> manifest, CancellationToken cancellationToken = default)
        {
            var syncItems = new List<ManifestItem>();

            foreach (var item in manifest)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return syncItems.ToArray();
                }

                if (!item.DestinationFile.StartsWith(LocalPath))
                {
                    throw new Exception("Computed path outside of local path.");
                }

                if (File.Exists(item.DestinationFile) && (await ComputeSHA1Async(item.DestinationFile, cancellationToken)) == item.Hash.ToLower())
                {
                    continue;
                }

                syncItems.Add(item);
            }

            return syncItems.ToArray();
        }

        /// <summary>
        /// Synchronizes files based on the manifest and SHA-1 hash values.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns>A task representing the asynchronous synchronization operation.</returns>
        public async Task SyncAsync(CancellationToken cancellationToken = default)
        {
            using var hc = new HttpClient();

            string manifestJson;

            manifestJson = await hc.GetStringAsync(this.ManifestUrl, cancellationToken);

            if (manifestJson != null)
            {
                await File.WriteAllTextAsync(Path.Combine(LocalPath, ManifestUrl.Segments.Last()), manifestJson, cancellationToken);
            }

            var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson)?.Select(item => new ManifestItem()
            {
                Key = item.Key,
                Hash = item.Value,
                DestinationFile = Path.GetFullPath(Path.Combine(this.LocalPath, item.Key)),
                SourceUrl = new Uri(BaseUrl, item.Key).ToString()
            }).ToArray();

            if (manifest == null)
            {
                throw new FormatException();
            }

            var syncItems = await GetSyncItemsAsync(manifest, cancellationToken);
            int itemIdx = 0;

            foreach (var item in syncItems)
            {
                SyncProgress?.Invoke(this, new SyncProgressEventArgs(item, itemIdx, syncItems.Length));

                using (var response = await hc.GetAsync(item.SourceUrl))
                {
                    if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        throw new FileNotFoundException(item.SourceUrl);
                    }

                    Directory.CreateDirectory(item.DestinationFolder);

                    using (var destFile = File.Create(item.DestinationFile))
                    {
                        await response.Content.CopyToAsync(destFile, cancellationToken);
                    }
                }

                itemIdx++;
            }

            SyncProgress?.Invoke(this, new SyncProgressEventArgs(null, syncItems.Length, syncItems.Length));
        }

        /// <summary>
        /// A convenience method to instantiate the class, and start a sync with the specificied parameters.
        /// </summary>
        /// <param name="manifestUrl">The URL of the manifest used for synchronization.</param>
        /// <param name="localPath">The local path where files will be synchronized.</param>
        /// <param name="syncProgress">A method that will be invoked when progress changes.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <returns></returns>
        public static Task SyncAsync(string manifestUrl, string localPath, EventHandler<SyncProgressEventArgs>? syncProgress = null, CancellationToken cancellationToken = default)
        {
            var syncer = new Syncer(manifestUrl, localPath);

            if (syncProgress != null)
            {
                syncer.SyncProgress += syncProgress;
            }

            return syncer.SyncAsync(cancellationToken);
        }
    }
}