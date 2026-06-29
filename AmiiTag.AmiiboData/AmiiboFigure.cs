namespace AmiiTag.AmiiboData
{
    /// <summary>
    /// Represents an amiibo figure.
    /// </summary>
    public class AmiiboFigure
    {
        internal List<GameInfo> _Games { get; set; } = new();

        /// <summary>
        /// The unique identifier for the amiibo figure as a hex string starting with '0x'.
        /// </summary>
        public AmiiboId Id { get; private set; }

        /// <summary>
        /// A URI to access the image for the figure.
        /// </summary>
        public Uri? ImageUri { get; private set; }

        /// <summary>
        /// Whether to show the amiibo in a list.
        /// </summary>
        public bool IsVisible { get; internal set; }

        /// <summary>
        /// The name of the amiibo figure.
        /// </summary>
        public string Name { get; internal set; }

        /// <summary>
        /// The game series of the amiibo figure.
        /// </summary>
        public string GameSeries { get; internal set; }

        /// <summary>
        /// The character associated with the amiibo figure.
        /// </summary>
        public string Character { get; internal set; }

        /// <summary>
        /// The type of the amiibo figure.
        /// </summary>
        public string FigureType { get; internal set; }

        /// <summary>
        /// The series associated with the amiibo figure.
        /// </summary>
        public string Series { get; internal set; }

        /// <summary>
        /// Release dates for the associated amiibo figure.
        /// </summary>
        public AmiiboRelease Release { get; internal set; }

        /// <summary>
        /// Contains a list of games this figure can be used with.
        /// </summary>
        public IReadOnlyList<GameInfo> Games => _Games;

        internal void SetId(string value, Uri? baseImageUri = null, Dictionary<string, string>? hashDictionary = null)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(nameof(value));
            }

            var amiiboMatch = RegexPatterns.AmiiboRegex.Match(value.Trim().ToLower());

            if (amiiboMatch.Success == false)
            {
                throw new ArgumentException(nameof(value));
            }

            value = amiiboMatch.Groups[1].Value;
            Id = new AmiiboId(value);

            var imageFilename = $"icon_{Id.Head}-{Id.Tail}.png";
            var imageKey = $"images/{imageFilename}";

            ImageUri = null;

            if (baseImageUri != null)
            {
                if (hashDictionary == null)
                {
                    ImageUri = new Uri(baseImageUri, $"{imageFilename}");
                }
                else if (hashDictionary.ContainsKey(imageKey))
                {
                    ImageUri = new Uri(baseImageUri, $"{imageFilename}?{hashDictionary[imageKey]}");
                }
            }
        }

        /// <summary>
        /// Creates a new instance with the same set of values.
        /// </summary>
        /// <returns></returns>
        internal AmiiboFigure Clone()
        {
            return new AmiiboFigure()
            {
                Id = Id,
                IsVisible = IsVisible,
                Name = Name,
                GameSeries = GameSeries,
                Character = Character,
                FigureType = FigureType,
                Series = Series,
                Release = new AmiiboRelease()
                {
                    Australia = Release.Australia,
                    Europe = Release.Europe,
                    Japan = Release.Japan,
                    NorthAmerica = Release.NorthAmerica,
                },
                ImageUri = ImageUri
            };
        }
    }
}
