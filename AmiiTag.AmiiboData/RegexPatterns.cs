using System.Text.RegularExpressions;

namespace AmiiTag.AmiiboData
{
    internal static class RegexPatterns
    {
        /// <summary>
        /// Regular expression for validating Amiibo IDs.
        /// Matches a 16-character hexadecimal string ending with "02" (NTAG215) or "03" (v3), optionally prefixed with "0x".
        /// </summary>
        public static readonly Regex AmiiboRegex = new Regex(@"^(?:0x)?([a-f0-9]{14}0[23])$", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for validating Amiibo Series IDs.
        /// Matches a 2-character hexadecimal string, optionally prefixed with "0x".
        /// </summary>
        public static readonly Regex AmiiboSeriesRegex = new Regex(@"^(?:0x)?([a-f0-9]{2})$", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for validating Amiibo Character IDs.
        /// Matches a 4-character hexadecimal string, optionally prefixed with "0x".
        /// </summary>
        public static readonly Regex AmiiboCharacterRegex = new Regex(@"^(?:0x)?([a-f0-9]{4})$", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for validating Amiibo Game Series IDs.
        /// Matches a 3-character hexadecimal string, optionally prefixed with "0x".
        /// </summary>
        public static readonly Regex AmiiboGameSeriesRegex = new Regex(@"^(?:0x)?([a-f0-9]{3})$", RegexOptions.Compiled);

        /// <summary>
        /// Regular expression for validating Amiibo Type IDs.
        /// Matches a 2-character hexadecimal string, optionally prefixed with "0x".
        /// </summary>
        public static readonly Regex AmiiboTypeRegex = new Regex(@"^(?:0x)?[a-f0-9]{2}$", RegexOptions.Compiled);
    }
}
