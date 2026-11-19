using System.Text;

namespace DanTheMan827.Helpers.Extensions
{
    /// <summary>
    /// Extension methods for byte arrays to convert them to hexadecimal strings.
    /// </summary>
    public static class ByteHexExtension
    {
        /// <summary>
        /// Converts a byte array to a hexadecimal string.
        /// </summary>
        /// <param name="bytes">The byte array to convert.</param>
        /// <param name="prepend0x">If true, prepends "0x" to the resulting string.</param>
        /// <returns>The hexadecimal string representation of the byte array.</returns>
        public static string ToHexString(this byte[] bytes, bool prepend0x = false)
        {
            // Check if the byte array is empty
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            // StringBuilder to efficiently build the hexadecimal string
            StringBuilder sb = new StringBuilder(bytes.Length * 2);

            // Optionally prepend "0x" to the resulting string
            if (prepend0x)
            {
                sb.Append("0x");
            }

            // Convert each byte to a 2-digit hexadecimal representation
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            // Return the final hexadecimal string
            return sb.ToString();
        }
    }
}
