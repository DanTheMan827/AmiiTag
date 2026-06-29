using System.Text.Json;

namespace DanTheMan827.Helpers
{
    public static class Web
    {
        public static async Task<T?> DownloadJsonAsync<T>(string url, CancellationToken cancellationToken = default)
        {
            using (var wc = new HttpClient())
            {
                var json = await wc.GetStreamAsync(url, cancellationToken);

                return await JsonSerializer.DeserializeAsync<T>(json);
            }
        }
        public static async Task<T?> DownloadJsonAsync<T>(Uri url, CancellationToken cancellationToken = default)
        {
            using (var wc = new HttpClient())
            {
                var json = await wc.GetStreamAsync(url, cancellationToken);

                return await JsonSerializer.DeserializeAsync<T>(json);
            }
        }
    }
}
