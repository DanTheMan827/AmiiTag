namespace AmiiTag.AmiiboData
{
    public class AmiiboId
    {
        /// <summary>
        /// The full identifier.
        /// </summary>
        public string Id { get; private set; }

        /// <summary>
        /// The first half of the identifier.
        /// </summary>
        public string Head { get; private set; }

        /// <summary>
        /// The second half of the identifier.
        /// </summary>
        public string Tail { get; private set; }

        /// <summary>
        /// The identifier for the game series as a hex string starting with '0x'.
        /// </summary>
        public string GameSeries { get; private set; }

        /// <summary>
        /// The combined idenfier for the character and variant.
        /// </summary>
        public string FullCharacter { get; private set; }

        /// <summary>
        /// The identifier for the character as a hex string starting with '0x'.
        /// </summary>
        public string Character { get; private set; }

        /// <summary>
        /// The identifier for the variation as a hex string starting with '0x'.
        /// </summary>
        public string Variation { get; private set; }

        /// <summary>
        /// The identifier for the type as a hex string starting with '0x'.
        /// </summary>
        public string FigureType { get; private set; }

        /// <summary>
        /// The identifier for the model number as a hex string starting with '0x'.
        /// </summary>
        public string ModelNumber { get; private set; }

        /// <summary>
        /// The identifier for the series as a hex string starting with '0x'.
        /// </summary>
        public string Series { get; private set; }
        public AmiiboId(string id)
        {
            var amiiboMatch = RegexPatterns.AmiiboRegex.Match(id.Trim().ToLower());

            if (amiiboMatch.Success == false)
            {
                throw new ArgumentException(nameof(id));
            }

            Id = amiiboMatch.Groups[1].Value;
            FullCharacter = Id.Substring(0, 4);
            GameSeries = Id.Substring(0, 3);
            Series = Id.Substring(12, 2);
            FigureType = Id.Substring(6, 2);
            Character = Id.Substring(0, 2);
            Variation = Id.Substring(2, 2);
            ModelNumber = Id.Substring(8, 4);
            Head = Id.Substring(0, 8);
            Tail = Id.Substring(8, 8);
        }

        public override string ToString() => Id;
    }
}
