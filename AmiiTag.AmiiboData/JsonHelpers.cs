using System.Text.Json;

namespace AmiiTag.AmiiboData
{
    internal class JsonHelpers<T>
    {
        public static async Task<T?> DeserializeJsonAsync(Stream jsonStream, CancellationToken cancellationToken = default) =>
            await JsonSerializer.DeserializeAsync<T>(jsonStream, cancellationToken: cancellationToken);

        public static async Task<T?> DeserializeJsonFileAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(path))
            {
                return default;
            }

            using (var file = File.OpenRead(path))
            {
                return await DeserializeJsonAsync(file, cancellationToken);
            }
        }
    }
}
