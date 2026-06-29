using AmiiTag.AmiiboData;
using DanTheMan827.ShaSync;


var dataPath = AppDomain.CurrentDomain.BaseDirectory;
var amiiboApiPath = Path.Combine(dataPath, "N3evin.AmiiboAPI");
var unofficialAmiiboDbPath = Path.Combine(dataPath, "DanTheMan827.UnofficialAmiiboDB");
var amiiboApiUrl = "https://dantheman827.github.io/AmiiboData";
var unofficialAmiiboDbUrl = "https://dantheman827.github.io/UnofficialAmiiboDB";

await Syncer.SyncAsync($"{amiiboApiUrl}/sha1sum.json", amiiboApiPath, Syncer_SyncProgress);
await Syncer.SyncAsync($"{unofficialAmiiboDbUrl}/sha1sum.json", unofficialAmiiboDbPath, Syncer_SyncProgress);

var localDatabase = new AmiiboDatabase();

await localDatabase.ImportData(amiiboApiPath);
await localDatabase.ImportData(unofficialAmiiboDbPath);

var remoteDatabase = new AmiiboDatabase();

await remoteDatabase.ImportData(amiiboApiUrl);
await remoteDatabase.ImportData(unofficialAmiiboDbUrl);

return;

void Syncer_SyncProgress(object? sender, Syncer.SyncProgressEventArgs e)
{
    if (e.Index == e.Total)
    {
        Console.WriteLine("Done!");

        return;
    }

    Console.WriteLine($"({e.Index + 1} / {e.Total}) {e?.SyncItem?.Key}");
}