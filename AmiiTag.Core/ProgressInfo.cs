namespace AmiiTag.Core
{
    public class ProgressInfo
    {
        public int Current { get; set; } = 0;
        public int Total { get; set; } = 0;
        public string Message { get; set; } = string.Empty;

        public ProgressInfo(int current, int total, string message)
        {
            this.Current = current;
            this.Total = total;
            this.Message = message;
        }
    }
}
