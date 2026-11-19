using DanTheMan827.ShaSync;
using DanTheMan827.TempFolders;

using (var temp = new EasyTempFolder("AmiiboAPI"))
{
    Console.WriteLine($"Using Path: {temp.Path}\n");

    var syncer = new Syncer("https://raw.githubusercontent.com/N3evin/AmiiboAPI/master/sha1sum.json", temp.Path);
    syncer.SyncProgress += Syncer_SyncProgress;

    await syncer.SyncAsync();
}

void Syncer_SyncProgress(object? sender, Syncer.SyncProgressEventArgs e)
{
    if (e.Index == e.Total)
    {
        Console.WriteLine("Done!");

        return;
    }

    Console.WriteLine($"({e.Index + 1} / {e.Total}) {e.SyncItem.Key}");
}