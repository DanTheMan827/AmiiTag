using AmiiTag.AmiiboData;
using AmiiTag.Core;
using AmiiTag.Core.Interfaces;

namespace AmiiTag.Maui.Data
{
    internal class AmiiboDatabaseContainer : IDatabaseContainer
    {
        public AmiiboDatabase Database { get; private set; } = new();

        public Task CheckUpdate(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task ClearData(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task LoadData(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task UpdateData(Func<ProgressInfo, Task>? progress = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        Task<bool> IDatabaseContainer.CheckUpdate(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
