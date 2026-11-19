namespace AmiiTag.AmiiboData
{
    public class AmiiboRelease
    {
        public DateTime? Australia { get; internal set; }
        public DateTime? Europe { get; internal set; }
        public DateTime? Japan { get; internal set; }
        public DateTime? NorthAmerica { get; internal set; }

        internal AmiiboRelease()
        {
            Australia = null;
            Europe = null;
            Japan = null;
            NorthAmerica = null;
        }
    }
}
