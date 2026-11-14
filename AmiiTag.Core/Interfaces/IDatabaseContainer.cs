using AmiiTag.AmiiboData;

namespace AmiiTag.Core.Interfaces
{
    public interface IDatabaseContainer
    {
        /// <summary>
        /// The database.
        /// </summary>
        public AmiiboDatabase Database { get; }

        /// <summary>
        /// True once data has been loaded into <see cref="Database"/>.
        /// </summary>
        public bool IsLoaded { get; }

        /// <summary>
        /// True if <see cref="LoadData"/> downloads data (the web), so the app should show progress for it.
        /// </summary>
        public bool LoadsRemotely { get; }

        /// <summary>
        /// Raised after data is (re)loaded.
        /// </summary>
        public event EventHandler? DataChanged;

        /// <summary>
        /// Updates the underlying data source and loads the new data.
        /// </summary>
        /// <param name="progress">A function that will be called for progress updates.</param>
        /// <returns></returns>
        public Task UpdateData(Func<ProgressInfo, Task>? progress = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Loads the data from the underlying data source.
        /// </summary>
        /// <returns></returns>
        public Task LoadData(CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears the data from the database instance.
        /// </summary>
        /// <returns></returns>
        public Task ClearData(CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks if there is updated data available and returns true if there is.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<bool> CheckUpdate(CancellationToken cancellationToken = default);
    }
}
