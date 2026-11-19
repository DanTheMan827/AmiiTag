using System.Security.Cryptography;
using System.Text;

namespace DanTheMan827.Helpers
{
    /// <summary>
    /// Provides helper methods for computing hash values using a specified HashAlgorithm type.
    /// </summary>
    /// <typeparam name="T">The type of the HashAlgorithm (e.g., SHA1, MD5).</typeparam>
    public static class HashHelpers
    {
        /// <summary>
        /// Computes the hash for the data in the specified stream.
        /// </summary>
        /// <param name="stream">The input stream containing the data.</param>
        /// <returns>The hash represented as a byte array.</returns>
        public static byte[] ComputeHash<T>(Stream stream) where T : HashAlgorithm
        {
            using (var algorithm = CallCreate<T>())
            {
                byte[] hashBytes = algorithm.ComputeHash(stream);
                return hashBytes;
            }
        }

        /// <summary>
        /// Computes the hash for the specified byte array.
        /// </summary>
        /// <param name="bytes">The input byte array.</param>
        /// <returns>The hash represented as a byte array.</returns>
        public static byte[] ComputeHash<T>(byte[] bytes) where T : HashAlgorithm
        {
            using (var algorithm = CallCreate<T>())
            {
                byte[] hashBytes = algorithm.ComputeHash(bytes);
                return hashBytes;
            }
        }

        /// <summary>
        /// Computes the hash for the specified input string.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>The hash represented as a byte array.</returns>
        public static byte[] ComputeHash<T>(string input) where T : HashAlgorithm
        {
            using (var algorithm = CallCreate<T>())
            {
                byte[] inputBytes = Encoding.UTF8.GetBytes(input);
                byte[] hashBytes = algorithm.ComputeHash(inputBytes);
                return hashBytes;
            }
        }

        /// <summary>
        /// Computes the hash for the data in the specified stream asynchronously.
        /// </summary>
        /// <param name="stream">The input stream containing the data.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the asynchronous operation.</param>
        /// <returns>The hash represented as a byte array.</returns>
        public static async Task<byte[]> ComputeHashAsync<T>(Stream stream, CancellationToken cancellationToken = default) where T : HashAlgorithm
        {
            using (var algorithm = CallCreate<T>())
            {
                byte[] hashBytes = await algorithm.ComputeHashAsync(stream, cancellationToken);
                return hashBytes;
            }
        }

        /// <summary>
        /// Computes the hash for the specified byte array asynchronously.
        /// </summary>
        /// <param name="bytes">The input byte array.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the asynchronous operation.</param>
        /// <returns>The hash represented as a byte array.</returns>
        public static async Task<byte[]> ComputeHashAsync<T>(byte[] bytes, CancellationToken cancellationToken = default) where T : HashAlgorithm
        {
            using (var ms = new MemoryStream(bytes))
            {
                return await ComputeHashAsync<T>(ms, cancellationToken);
            }
        }

        /// <summary>
        /// Computes the hash for the specified input string asynchronously.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the asynchronous operation.</param>
        /// <returns>The hash represented as a byte array.</returns>
        public static async Task<byte[]> ComputeHashAsync<T>(string input, CancellationToken cancellationToken = default) where T : HashAlgorithm
        {
            return await ComputeHashAsync<T>(Encoding.UTF8.GetBytes(input), cancellationToken);
        }

        public static bool CanHash<T>() where T : HashAlgorithm
        {
            try
            {
                ComputeHash<T>("");
                return true;
            }
            catch (Exception ex) { }

            return false;
        }

        private static T CallCreate<T>() where T : HashAlgorithm
        {
            var algo = typeof(T);
            var createFunction = algo.GetMethods().Where(method => method.Name == "Create" && method.IsPublic && method.IsStatic && method.GetParameters().Length == 0).First();

            return createFunction.Invoke(null, null) as T;
        }
    }
}
