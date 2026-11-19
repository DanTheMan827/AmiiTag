using AmiiTag.AmiiboData.JSON.Amiibo;
using AmiiTag.AmiiboData.JSON.GameInfo;
using DanTheMan827.Helpers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AmiiTag.AmiiboData
{
    /// <summary>
    /// Represents an Amiibo Database with methods to import and process Amiibo data.
    /// </summary>
    public class AmiiboDatabase
    {
        /// <summary>
        /// Dictionary to store Amiibo Series data.
        /// Key: Amiibo Series ID, Value: Amiibo Series Name
        /// </summary>
        internal readonly Dictionary<string, string> _AmiiboSeries = new();

        /// <summary>
        /// Dictionary to store Amiibo Figure data.
        /// Key: Amiibo ID, Value: AmiiboFigure instance
        /// </summary>
        internal readonly Dictionary<string, AmiiboFigure> _Figures = new();

        /// <summary>
        /// Dictionary to store Amiibo Character data.
        /// Key: Amiibo Character ID, Value: Amiibo Character Name
        /// </summary>
        internal readonly Dictionary<string, string> _Characters = new();

        /// <summary>
        /// Dictionary to store Amiibo Game Series data.
        /// Key: Amiibo Game Series ID, Value: Amiibo Game Series Name
        /// </summary>
        internal readonly Dictionary<string, string> _GameSeries = new();

        /// <summary>
        /// Dictionary to store Amiibo Type data.
        /// Key: Amiibo Type ID, Value: Amiibo Type Name
        /// </summary>
        internal readonly Dictionary<string, string> _Types = new();

        /// <summary>
        /// Gets the Amiibo Series data.
        /// </summary>
        public IReadOnlyDictionary<string, string>? AmiiboSeries => _AmiiboSeries;

        /// <summary>
        /// Gets the Amiibo Figures data.
        /// </summary>
        public IReadOnlyDictionary<string, AmiiboFigure> Figures => _Figures;

        /// <summary>
        /// Gets the Characters data.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Characters => _Characters;

        /// <summary>
        /// Gets the Game Series data.
        /// </summary>
        public IReadOnlyDictionary<string, string>? GameSeries => _GameSeries;

        /// <summary>
        /// Gets the Types data.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Types => _Types;

        /// <summary>
        /// Imports amiibo data from a specified path, either local or remote.
        /// </summary>
        /// <param name="path">The path to the amiibo data, either a local directory or a remote URL.</param>
        /// <param name="imagesUrlBase">The base URL for amiibo images. Defaults to "images/" relative to the path.</param>
        /// <param name="cancellationToken">A token to cancel the import operation.</param>
        public async Task ImportData(string path, Uri? imagesUrlBase = null, CancellationToken cancellationToken = default, Func<Uri, CancellationToken, Task<Stream>>? download = null)
        {
            var lowercasePath = path.ToLower();

            // TODO: Load sha1sum.json to use hashes as cache busters.

            if (lowercasePath.StartsWith("http://") || lowercasePath.StartsWith("https://"))
            {
                if (!path.EndsWith("/"))
                {
                    path = path + "/";
                }

                await ImportRemoteData(new Uri(path), imagesUrlBase ?? new Uri(new Uri(path), "images/"), cancellationToken, download);
            }
            else
            {
                await ImportLocalData(path, imagesUrlBase ?? new Uri(new Uri($"file://{path}/"), "images/"), cancellationToken);
            }
        }

        private static async Task<T?> DownloadRemoteJson<T>(Uri url, Func<Uri, CancellationToken, Task<Stream>>? download, CancellationToken ct)
        {
            if (download == null) return await Web.DownloadJsonAsync<T>(url, ct);
            using var stream = await download(url, ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
        }

        /// <summary>Publish a fully loaded database without exposing a partially imported update.</summary>
        public void ReplaceData(AmiiboDatabase source)
        {
            if (ReferenceEquals(this, source)) return;
            ClearData();
            foreach (var item in source._AmiiboSeries) _AmiiboSeries.Add(item.Key, item.Value);
            foreach (var item in source._Figures) _Figures.Add(item.Key, item.Value);
            foreach (var item in source._Characters) _Characters.Add(item.Key, item.Value);
            foreach (var item in source._GameSeries) _GameSeries.Add(item.Key, item.Value);
            foreach (var item in source._Types) _Types.Add(item.Key, item.Value);
        }

        /// <summary>
        /// Clears all data in the AmiiboDatabase instance.
        /// </summary>
        public void ClearData()
        {
            _AmiiboSeries?.Clear();
            _Figures?.Clear();
            _Characters?.Clear();
            _GameSeries?.Clear();
            _Types?.Clear();
        }

        /// <summary>
        /// Imports amiibo data from a specified remote Uri.
        /// </summary>
        /// <param name="path">The path to the remote amiibo data.</param>
        /// <param name="imagesUrlBase">The base URL for amiibo images.</param>
        /// <param name="cancellationToken">A token to cancel the import operation.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        private async Task ImportRemoteData(Uri path, Uri? imagesUrlBase, CancellationToken cancellationToken = default, Func<Uri, CancellationToken, Task<Stream>>? download = null)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            var hashDictionary = new Dictionary<string, string>();
            var hashJsonPath = new Uri(path, $"sha1sum.json?{Guid.NewGuid().ToString()}");

            try
            {
                hashDictionary = await DownloadRemoteJson<Dictionary<string, string>>(hashJsonPath, download, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // It really doesn't matter if the sha1sum fails to load, we just won't have a cache buster.
            }

            // Load and process the amiibo database.
            {
                var amiiboJsonPath = new Uri(path, $"database/amiibo.json?{GetKeyOrDefaultValue(hashDictionary, "database/amiibo.json", download == null ? Guid.NewGuid().ToString() : string.Empty)}");
                var databaseJson = await DownloadRemoteJson<AmiiboJsonBase>(amiiboJsonPath, download, cancellationToken);

                if (databaseJson != null)
                {
                    await ProcessAmiiboJsonAsync(databaseJson, hashDictionary, imagesUrlBase, cancellationToken);
                }
            }

            // Load and process the game usage database.
            {
                var gameInfoJsonPath = new Uri(path, $"database/games_info.json?{GetKeyOrDefaultValue(hashDictionary, "database/games_info.json", download == null ? Guid.NewGuid().ToString() : string.Empty)}");
                GameInfoJsonBase? gameInfoJson = null;

                try
                {
                    gameInfoJson = await DownloadRemoteJson<GameInfoJsonBase>(gameInfoJsonPath, download, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // If there's an error loading the game info, we can just skip it.
                }

                // Process the game info if it deserialized successfully.
                if (gameInfoJson != null)
                {
                    await ProcessGameInfoJsonAsync(gameInfoJson, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Imports amiibo data from a specified remote Uri.
        /// </summary>
        /// <param name="path">The path to the remote amiibo data.</param>
        /// <param name="imagesUrlBase">The base URL for amiibo images.</param>
        /// <param name="cancellationToken">A token to cancel the import operation.</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="FileNotFoundException"></exception>
        private async Task ImportLocalData(string path, Uri? imagesUrlBase, CancellationToken cancellationToken = default)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            var hashJsonPath = Path.Combine(path, "sha1sum.json");
            var amiiboJsonPath = Path.Combine(path, "database", "amiibo.json");

            if (!File.Exists(amiiboJsonPath))
            {
                throw new FileNotFoundException(amiiboJsonPath);
            }

            var hashDictionary = new Dictionary<string, string>();

            try
            {
                if (File.Exists(hashJsonPath))
                {
                    using (var hashStream = File.OpenRead(hashJsonPath))
                    {
                        var hashes = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(hashStream);

                        hashDictionary = hashes;
                    }
                }
            }
            catch (Exception ex)
            {
                // It really doesn't matter if the sha1sum fails to load, we just won't have a cache buster.
            }

            // Load and process the amiibo database.
            {
                var databaseJson = await AmiiboJsonBase.DeserializeJsonFileAsync(amiiboJsonPath, cancellationToken);

                if (databaseJson != null)
                {
                    await ProcessAmiiboJsonAsync(databaseJson, hashDictionary, imagesUrlBase, cancellationToken);
                }
            }

            // Load and process the game usage database.
            {
                var gameInfoJsonPath = Path.Combine(path, "database", "games_info.json");
                if (File.Exists(gameInfoJsonPath))
                {

                    GameInfoJsonBase? gameInfoJson = null;

                    try
                    {
                        gameInfoJson = await GameInfoJsonBase.DeserializeJsonFileAsync(gameInfoJsonPath, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        // If there's an error loading the game info, we can just skip it.
                    }

                    // Process the game info if it deserialized successfully.
                    if (gameInfoJson != null && gameInfoJson.Amiibos.Count > 0)
                    {
                        await ProcessGameInfoJsonAsync(gameInfoJson, cancellationToken);
                    }
                }
            }
        }

        /// <summary>
        /// Processes amiibo JSON data asynchronously.
        /// </summary>
        /// <param name="json">The amiibo JSON data to process.</param>
        /// <param name="hashDictionary">A dictionary of hash values for cache busting.</param>
        /// <param name="imagesUrlBase">The base URL for amiibo images.</param>
        /// <param name="cancellationToken">A token to cancel the processing operation.</param>

        private async Task ProcessAmiiboJsonAsync(AmiiboJsonBase json, Dictionary<string, string>? hashDictionary = null, Uri? imagesUrlBase = null, CancellationToken cancellationToken = default)
        {
            await Task.Run(() =>
            {
                if (json == null)
                {
                    throw new ArgumentNullException(nameof(json));
                }

                // Add processed json dictionary values

                ProcessDictionary(json.AmiiboSeries, _AmiiboSeries, RegexPatterns.AmiiboSeriesRegex);
                ProcessDictionary(json.Characters, _Characters, RegexPatterns.AmiiboCharacterRegex);
                ProcessDictionary(json.GameSeries, _GameSeries, RegexPatterns.AmiiboGameSeriesRegex);
                ProcessDictionary(json.Types, _Types, RegexPatterns.AmiiboTypeRegex);

                if (json.AmiiboFigures != null)
                {
                    foreach (var figure in json.AmiiboFigures)
                    {
                        var figureKey = figure.Key.Trim().ToLower();

                        // Check if the ID matches the format.
                        var amiiboMatch = RegexPatterns.AmiiboRegex.Match(figureKey);

                        // Continue if it matches.
                        if (amiiboMatch.Success)
                        {
                            var amiiboId = amiiboMatch.Groups[1].Value;
                            var amiibo = new AmiiboFigure();

                            amiibo.SetId(amiiboId, imagesUrlBase, hashDictionary);
                            amiibo.Name = figure.Value.Name ?? "";
                            amiibo.IsVisible = figure.Value.IsVisible;
                            amiibo.Release = new AmiiboRelease
                            {
                                Australia = TryParseDate(figure.Value.Release?["au"]),
                                Europe = TryParseDate(figure.Value.Release?["eu"]),
                                Japan = TryParseDate(figure.Value.Release?["jp"]),
                                NorthAmerica = TryParseDate(figure.Value.Release?["na"])
                            };

                            amiibo.Character = GetKeyOrDefaultValue(_Characters, amiibo.Id.FullCharacter, "Unknown");
                            amiibo.Series = GetKeyOrDefaultValue(_AmiiboSeries, amiibo.Id.Series, "Unknown");
                            amiibo.GameSeries = GetKeyOrDefaultValue(_GameSeries, amiibo.Id.GameSeries, "Unknown");

                            if (figure.Value.MappedId != null && RegexPatterns.AmiiboRegex.IsMatch(figure.Value.MappedId) && _Figures.ContainsKey(figure.Value.MappedId))
                            {
                                var sourceAmiibo = _Figures[figure.Value.MappedId];

                                amiibo.Release.Australia = amiibo.Release.Australia ?? sourceAmiibo.Release.Australia;
                                amiibo.Release.Europe = amiibo.Release.Europe ?? sourceAmiibo.Release.Europe;
                                amiibo.Release.Japan = amiibo.Release.Japan ?? sourceAmiibo.Release.Japan;
                                amiibo.Release.NorthAmerica = amiibo.Release.NorthAmerica ?? sourceAmiibo.Release.NorthAmerica;

                                amiibo.Character = sourceAmiibo.Character;
                                amiibo.Series = sourceAmiibo.Series;
                                amiibo.GameSeries = sourceAmiibo.GameSeries;
                            }

                            amiibo.FigureType = GetKeyOrDefaultValue(_Types, amiibo.Id.FigureType, "Unknown");

                            if (!_Figures.ContainsKey(amiibo.Id.ToString()))
                            {
                                _Figures.Add(amiibo.Id.ToString(), amiibo);
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Processes amiibo game usage JSON data asynchronously.
        /// </summary>
        /// <param name="json"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ProcessGameInfoJsonAsync(GameInfoJsonBase json, CancellationToken cancellationToken = default)
        {
            await Task.Run(async () =>
            {
                var usages = new Dictionary<string, GameUsage>();
                var games = new Dictionary<string, GameInfo>();

                bool canHash = false;

                try
                {
                    await HashHelpers.ComputeHashAsync<MD5>("");
                    canHash = true;
                }
                catch (Exception ex) { }

                foreach (var item in json.Amiibos)
                {
                    var amiiboMatch = RegexPatterns.AmiiboRegex.Match(item.Key.ToLower());

                    // Continue if it matches.
                    if (amiiboMatch.Success)
                    {
                        var amiiboId = amiiboMatch.Groups[1].Value;
                        if (_Figures.ContainsKey(amiiboId))
                        {
                            var figure = _Figures[amiiboId];

                            foreach (var game in item.Value.Games3ds.Select(game => new GameInfo(game, SystemKind._3DS)).OrderBy(a => a.Name)
                                .Concat(item.Value.GamesSwitch.Select(game => new GameInfo(game, SystemKind._Switch)).OrderBy(a => a.Name))
                                .Concat(item.Value.GamesWiiU.Select(game => new GameInfo(game, SystemKind._WiiU)).OrderBy(a => a.Name)))
                            {
                                var gameHash = canHash ? game.GetHash() : Guid.NewGuid().ToString();
                                var gameInfo = game;

                                if (games.ContainsKey(gameHash))
                                {
                                    gameInfo = games[gameHash];
                                }
                                else
                                {
                                    var gameUsages = gameInfo._Usage.ToArray();
                                    gameInfo._Usage.Clear();

                                    foreach (var usage in gameUsages)
                                    {
                                        var usageHash = canHash ? usage.GetHash() : Guid.NewGuid().ToString();

                                        if (!usages.ContainsKey(usageHash))
                                        {
                                            usages.Add(usageHash, usage);
                                        }

                                        gameInfo._Usage.Add(usages[usageHash]);
                                    }

                                    games.Add(gameHash, gameInfo);
                                }

                                figure._Games.Add(gameInfo);
                            }
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Attempts to parse a date string in the "yyyy-MM-dd" format.
        /// </summary>
        /// <param name="date">The date string to parse.</param>
        /// <returns>A nullable DateTime representing the parsed date, or null if parsing fails.</returns>
        private DateTime? TryParseDate(string? date)
        {
            if (date == null)
            {
                return null;
            }

            DateTime result;

            if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            {
                return result;
            }

            return null;
        }

        /// <summary>
        /// Retrieves the value associated with the specified key from a dictionary.
        /// If the key is not found, returns a default value.
        /// </summary>
        /// <typeparam name="K">The type of the keys in the dictionary.</typeparam>
        /// <typeparam name="T">The type of the values in the dictionary.</typeparam>
        /// <param name="dict">The dictionary to retrieve the value from.</param>
        /// <param name="key">The key to look up in the dictionary.</param>
        /// <param name="defaultValue">The default value to return if the key is not found.</param>
        /// <returns>The value associated with the key, or the default value if the key is not found.</returns>
        private T GetKeyOrDefaultValue<K, T>(Dictionary<K, T> dict, K key, T defaultValue)
        {
            if (dict.ContainsKey(key))
            {
                return dict[key];
            }

            return defaultValue;
        }

        /// <summary>
        /// Processes a dictionary, transforming and adding its entries to another dictionary.
        /// </summary>
        /// <param name="inDictionary">The input dictionary to process.</param>
        /// <param name="outDictionary">The output dictionary to populate.</param>
        /// <param name="keyValidator">A regular expression to validate keys before processing.</param>
        private void ProcessDictionary(Dictionary<string, string>? inDictionary, Dictionary<string, string> outDictionary, Regex keyValidator)
        {
            if (inDictionary != null)
            {
                foreach (var item in inDictionary)
                {
                    var inKey = item.Key.ToLower().Trim().Substring(2);
                    var inValue = item.Value?.Trim() ?? "";

                    if (String.IsNullOrEmpty(inValue))
                    {
                        inValue = "Unknown";
                    }

                    if (keyValidator.IsMatch(item.Key) && !outDictionary.ContainsKey(inKey))
                    {
                        outDictionary.Add(inKey, inValue);
                    }
                }
            }
        }
    }
}
