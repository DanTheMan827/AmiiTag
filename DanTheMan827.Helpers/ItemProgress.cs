namespace DanTheMan827.Helpers
{
    public sealed class ItemProgress : IProgress<double>
    {
        private readonly IProgress<double> _output;
        private double _itemProgress;

        public ItemProgress(int itemCount, IProgress<double> output)
        {
            if (itemCount < 0)
                throw new ArgumentOutOfRangeException(nameof(itemCount));

            _output = output ?? throw new ArgumentNullException(nameof(output));
            ItemCount = itemCount;

            Publish();
        }

        public int ItemCount
        {
            get;
        }

        public int CompletedItems
        {
            get; private set;
        }

        public double OverallProgress =>
            ItemCount == 0
                ? 1.0
                : (CompletedItems + _itemProgress) / ItemCount;

        public void Report(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value));

            if (CompletedItems == ItemCount)
                return;

            // Prevent progress moving backward within the current item.
            _itemProgress = Math.Max(
                _itemProgress,
                Math.Clamp(value, 0.0, 1.0));

            Publish();
        }

        public void CompleteItem()
        {
            if (CompletedItems >= ItemCount)
                throw new InvalidOperationException("All items are complete.");

            CompletedItems++;
            _itemProgress = 0;

            Publish();
        }

        private void Publish() => _output.Report(OverallProgress);
    }
}
