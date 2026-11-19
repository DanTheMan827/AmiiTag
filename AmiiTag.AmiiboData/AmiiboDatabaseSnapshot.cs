using System.Text;

namespace AmiiTag.AmiiboData
{
    /// <summary>
    /// A compact binary copy of a fully loaded <see cref="AmiiboDatabase"/>: the lookup tables, every figure (with its
    /// image URL), and the games and usages, each written once and shared by index as in the loaded database. Reading it
    /// back is a straight pass over the bytes, with none of the JSON parsing, ID matching and de-duplication an import
    /// does, which is what makes loading the database slow in the browser.
    /// </summary>
    public static class AmiiboDatabaseSnapshot
    {
        /// <summary>
        /// Changes whenever the layout does, so a stored snapshot in an older layout is never read.
        /// </summary>
        public const int FormatVersion = 1;

        /// <summary>
        /// The build of the code that writes and reads snapshots: this assembly's module version ID, which a
        /// deterministic build derives from the compiled code, so it changes whenever the model or this class changes
        /// (and stays the same when they don't). A snapshot is only read by the same build that wrote it, so a model
        /// change can't be misread from an old snapshot even if <see cref="FormatVersion"/> wasn't bumped.
        /// </summary>
        public static Guid BuildId { get; } = typeof(AmiiboDatabaseSnapshot).Assembly.ManifestModule.ModuleVersionId;

        /// <summary>
        /// Identifies the snapshot layout and the build that uses it; part of the key a stored snapshot is found by.
        /// </summary>
        public static string SchemaId => $"{FormatVersion}:{BuildId:N}";

        private static readonly byte[] Magic = "ATDB"u8.ToArray();

        public static byte[] Write(AmiiboDatabase database)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(BuildId.ToByteArray());
                WriteMap(writer, database._AmiiboSeries);
                WriteMap(writer, database._Characters);
                WriteMap(writer, database._GameSeries);
                WriteMap(writer, database._Types);

                // Games and usages are shared between figures; each is written once.
                var games = new List<GameInfo>();
                var gameIndex = new Dictionary<GameInfo, int>(ReferenceEqualityComparer.Instance);
                var usages = new List<GameUsage>();
                var usageIndex = new Dictionary<GameUsage, int>(ReferenceEqualityComparer.Instance);
                foreach (var figure in database._Figures.Values)
                {
                    foreach (var game in figure._Games)
                    {
                        if (gameIndex.TryAdd(game, games.Count))
                        {
                            games.Add(game);
                            foreach (var usage in game._Usage)
                            {
                                if (usageIndex.TryAdd(usage, usages.Count))
                                {
                                    usages.Add(usage);
                                }
                            }
                        }
                    }
                }

                writer.Write(usages.Count);
                foreach (var usage in usages)
                {
                    writer.Write(usage.Description ?? string.Empty);
                    writer.Write(usage.Writable);
                }

                writer.Write(games.Count);
                foreach (var game in games)
                {
                    writer.Write(game.Name ?? string.Empty);
                    writer.Write((int)game.System);
                    writer.Write(game._Ids.Count);
                    foreach (var id in game._Ids)
                    {
                        writer.Write(id ?? string.Empty);
                    }

                    writer.Write(game._Usage.Count);
                    foreach (var usage in game._Usage)
                    {
                        writer.Write(usageIndex[usage]);
                    }
                }

                writer.Write(database._Figures.Count);
                foreach (var (key, figure) in database._Figures)
                {
                    writer.Write(key);
                    writer.Write(figure.Id.ToString());
                    WriteNullable(writer, figure.ImageUri?.AbsoluteUri);
                    writer.Write(figure.IsVisible);
                    writer.Write(figure.Name ?? string.Empty);
                    writer.Write(figure.GameSeries ?? string.Empty);
                    writer.Write(figure.Character ?? string.Empty);
                    writer.Write(figure.FigureType ?? string.Empty);
                    writer.Write(figure.Series ?? string.Empty);
                    WriteDate(writer, figure.Release?.Australia);
                    WriteDate(writer, figure.Release?.Europe);
                    WriteDate(writer, figure.Release?.Japan);
                    WriteDate(writer, figure.Release?.NorthAmerica);
                    writer.Write(figure._Games.Count);
                    foreach (var game in figure._Games)
                    {
                        writer.Write(gameIndex[game]);
                    }
                }
            }

            return stream.ToArray();
        }

        /// <summary>
        /// The database a snapshot holds, or null if it isn't one (wrong layout version, truncated or damaged).
        /// </summary>
        public static AmiiboDatabase? TryRead(byte[] data)
        {
            try
            {
                using var reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
                if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion ||
                    !reader.ReadBytes(16).AsSpan().SequenceEqual(BuildId.ToByteArray()))
                {
                    return null;
                }

                var database = new AmiiboDatabase();
                ReadMap(reader, database._AmiiboSeries);
                ReadMap(reader, database._Characters);
                ReadMap(reader, database._GameSeries);
                ReadMap(reader, database._Types);

                var usages = new GameUsage[ReadCount(reader)];
                for (var i = 0; i < usages.Length; i++)
                {
                    usages[i] = new GameUsage { Description = reader.ReadString(), Writable = reader.ReadBoolean() };
                }

                var games = new GameInfo[ReadCount(reader)];
                for (var i = 0; i < games.Length; i++)
                {
                    var game = new GameInfo { Name = reader.ReadString(), System = (SystemKind)reader.ReadInt32() };
                    var ids = ReadCount(reader);
                    for (var j = 0; j < ids; j++)
                    {
                        game._Ids.Add(reader.ReadString());
                    }

                    var gameUsages = ReadCount(reader);
                    for (var j = 0; j < gameUsages; j++)
                    {
                        game._Usage.Add(usages[reader.ReadInt32()]);
                    }

                    games[i] = game;
                }

                var figures = ReadCount(reader);
                for (var i = 0; i < figures; i++)
                {
                    var key = reader.ReadString();
                    var figure = new AmiiboFigure();
                    var id = reader.ReadString();
                    var image = ReadNullable(reader);
                    figure.Restore(new AmiiboId(id), image == null ? null : new Uri(image));
                    figure.IsVisible = reader.ReadBoolean();
                    figure.Name = reader.ReadString();
                    figure.GameSeries = reader.ReadString();
                    figure.Character = reader.ReadString();
                    figure.FigureType = reader.ReadString();
                    figure.Series = reader.ReadString();
                    figure.Release = new AmiiboRelease
                    {
                        Australia = ReadDate(reader),
                        Europe = ReadDate(reader),
                        Japan = ReadDate(reader),
                        NorthAmerica = ReadDate(reader),
                    };

                    var figureGames = ReadCount(reader);
                    for (var j = 0; j < figureGames; j++)
                    {
                        figure._Games.Add(games[reader.ReadInt32()]);
                    }

                    database._Figures[key] = figure;
                }

                return reader.BaseStream.Position == reader.BaseStream.Length ? database : null;
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                return null;
            }
        }

        private static void WriteMap(BinaryWriter writer, Dictionary<string, string> map)
        {
            writer.Write(map.Count);
            foreach (var (key, value) in map)
            {
                writer.Write(key);
                writer.Write(value ?? string.Empty);
            }
        }

        private static void ReadMap(BinaryReader reader, Dictionary<string, string> map)
        {
            var count = ReadCount(reader);
            for (var i = 0; i < count; i++)
            {
                map[reader.ReadString()] = reader.ReadString();
            }
        }

        private static void WriteNullable(BinaryWriter writer, string? value)
        {
            writer.Write(value != null);
            if (value != null)
            {
                writer.Write(value);
            }
        }

        private static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

        private static void WriteDate(BinaryWriter writer, DateTime? date)
        {
            writer.Write(date?.Ticks ?? -1L);
        }

        private static DateTime? ReadDate(BinaryReader reader)
        {
            var ticks = reader.ReadInt64();
            return ticks < 0 ? null : new DateTime(ticks);
        }

        private static int ReadCount(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > reader.BaseStream.Length)
            {
                throw new FormatException("Invalid count in database snapshot.");
            }

            return count;
        }
    }
}
