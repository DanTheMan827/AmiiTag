using System.Text;
using AmiiTag.Bluetooth;
using AmiiTag.Bluetooth.Devices;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// The Pixl.js driver against a simulated Pixl.js speaking the documented BLE file protocol, and device detection on
/// the shared Nordic UART service.
/// </summary>
public class PixlTests
{
    private static async Task<(PixlSimulator Sim, PixlDevice Device)> ConnectAsync(int mtu = 247)
    {
        var sim = new PixlSimulator { Mtu = mtu };
        sim.AddFolder("E:/amiibo");
        var device = new PixlDevice(sim);
        Assert.True(await device.InitializeAsync());
        return (sim, device);
    }

    [Fact]
    public void Frames_AreNumberedAndFlagContinuation()
    {
        var payload = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        var frames = PixlDevice.BuildFrames(0x19, payload, 243);

        Assert.Equal(3, frames.Count);
        Assert.Equal(new byte[] { 0x19, 0x00, 0x00, 0x80 }, frames[0][..4]);
        Assert.Equal(new byte[] { 0x19, 0x00, 0x01, 0x80 }, frames[1][..4]);
        Assert.Equal(new byte[] { 0x19, 0x00, 0x02, 0x00 }, frames[2][..4]);
        Assert.Equal(payload, frames.SelectMany(f => f[4..]).ToArray());
    }

    [Fact]
    public void Frames_EmptyPayloadIsOneHeader()
    {
        var frames = PixlDevice.BuildFrames(0x01, []);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, Assert.Single(frames));
    }

    [Fact]
    public async Task Initialize_ReadsVersionAndAddress()
    {
        var (sim, device) = await ConnectAsync();

        Assert.Equal("2.11.0", device.Firmware);
        Assert.Equal("C3:5B:9E:11:22:33", device.Address);
        Assert.Equal(TagDeviceKind.Pixl, device.Kind);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, sim.Received[0]);
    }

    [Fact]
    public async Task Initialize_AcceptsVersionWithoutAddress()
    {
        var sim = new PixlSimulator { Address = null };
        var device = new PixlDevice(sim);

        Assert.True(await device.InitializeAsync());
        Assert.Equal("2.11.0", device.Firmware);
        Assert.Null(device.Address);
    }

    [Fact]
    public async Task Drives_AreParsed()
    {
        var (sim, device) = await ConnectAsync();
        sim.AddFile("E:/amiibo/a.bin", new byte[540]);

        var drive = Assert.Single(await device.GetDrivesAsync());

        Assert.Equal('E', drive.Label);
        Assert.Equal("External Flash", drive.Name);
        Assert.True(drive.IsAvailable);
        Assert.Equal(sim.DriveSize, drive.TotalBytes);
        Assert.Equal(540, drive.UsedBytes);
        Assert.Equal("E:/", drive.RootPath);
    }

    [Theory]
    [InlineData(247)]
    [InlineData(185)]
    [InlineData(0)]
    public async Task Files_RoundTripAcrossManyFrames(int mtu)
    {
        var (sim, device) = await ConnectAsync(mtu);
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var progress = new List<TransferProgress>();

        await device.WriteFileAsync("E:/amiibo/big.bin", data, new SyncProgress<TransferProgress>(progress.Add));

        Assert.Equal(data, sim.Files["E:/amiibo/big.bin"]);
        Assert.Equal(0, sim.OpenHandles);
        Assert.Equal(data.Length, progress[^1].Current);

        var read = await device.ReadFileAsync("E:/amiibo/big.bin", data.Length);
        Assert.Equal(data, read);
        Assert.Equal(0, sim.OpenHandles);
        Assert.Equal(0, sim.SplitRequests);
    }

    [Fact]
    public async Task WriteFile_StreamsFramesWithTheFileIdAndOneReply()
    {
        var (sim, device) = await ConnectAsync();
        var data = new byte[600];

        await device.WriteFileAsync("E:/a.bin", data);

        var frames = sim.Received.Where(f => f[0] == PixlDevice.CmdWriteFile).ToList();
        Assert.Equal(3, frames.Count); // 242 + 242 + 116
        Assert.Equal(new byte[] { 0x00, 0x80 }, frames[0][2..4]);
        Assert.Equal(new byte[] { 0x01, 0x80 }, frames[1][2..4]);
        Assert.Equal(new byte[] { 0x02, 0x00 }, frames[2][2..4]);
        Assert.All(frames, f => Assert.Equal(0, f[4]));
        Assert.Equal(600, sim.Files["E:/a.bin"].Length);
    }

    [Fact]
    public void WriteFrames_HandleEmptyFiles()
    {
        var frame = Assert.Single(PixlDevice.BuildWriteFrames(0, []));
        Assert.Equal(new byte[] { PixlDevice.CmdWriteFile, 0, 0, 0, 0 }, frame);
    }

    [Fact]
    public async Task OpenFile_UsesTheWebAppsModes()
    {
        var (sim, device) = await ConnectAsync();

        await device.WriteFileAsync("E:/x.bin", [1, 2, 3]);
        await device.ReadFileAsync("E:/x.bin");

        // 32 bit vfs_mode_t flags: truncate | create | write only, then read only.
        var opens = sim.Requests.Where(r => r.Command == PixlDevice.CmdOpenFile).ToList();
        Assert.Equal(new byte[] { 0x16, 0, 0, 0 }, opens[0].Payload[^4..]);
        Assert.Equal(new byte[] { 0x08, 0, 0, 0 }, opens[1].Payload[^4..]);
    }

    [Fact]
    public async Task ReadFolder_ListsFoldersFirstWithMeta()
    {
        var (sim, device) = await ConnectAsync();
        sim.AddFolder("E:/amiibo/fav");
        sim.AddFile("E:/amiibo/zelda.bin", new byte[540], [.. PixlDevice.EncodeMeta(new PixlFileMeta("Hi", Hidden: true, AmiiboId: "01000000034c0902"))]);
        sim.AddFile("E:/amiibo/a.bin", new byte[572]);

        var entries = await device.ReadFolderAsync("E:/amiibo");

        Assert.Equal(new[] { "fav", "a.bin", "zelda.bin" }, entries.Select(e => e.Name));
        Assert.True(entries[0].IsFolder);
        Assert.Equal("E:/amiibo/fav", entries[0].Path);
        Assert.Equal(572, entries[1].Size);
        Assert.True(entries[1].Meta.IsEmpty);
        Assert.Equal("Hi", entries[2].Meta.Notes);
        Assert.True(entries[2].Meta.Hidden);
        Assert.Equal("01000000034c0902", entries[2].Meta.AmiiboId);
    }

    [Fact]
    public async Task ReadFolder_ReassemblesLongListings()
    {
        var (sim, device) = await ConnectAsync();
        for (var i = 0; i < 40; i++)
        {
            sim.AddFile($"E:/amiibo/figure-{i:00}.bin", new byte[540]);
        }

        var entries = await device.ReadFolderAsync("E:/amiibo");

        Assert.Equal(40, entries.Count);
    }

    [Fact]
    public async Task FolderOperations_Work()
    {
        var (sim, device) = await ConnectAsync();

        await device.CreateFolderAsync("E:/amiibo/new");
        Assert.Contains("E:/amiibo/new", sim.Folders);

        await device.WriteFileAsync("E:/amiibo/new/a.bin", [1]);
        await device.RenameAsync("E:/amiibo/new/a.bin", "E:/amiibo/new/b.bin");
        Assert.True(sim.Files.ContainsKey("E:/amiibo/new/b.bin"));

        await device.UpdateMetaAsync("E:/amiibo/new/b.bin", new PixlFileMeta("note"));
        Assert.Equal("note", PixlDevice.ParseMeta(sim.Meta["E:/amiibo/new/b.bin"]).Notes);

        await device.DeleteAsync("E:/amiibo/new");
        Assert.DoesNotContain("E:/amiibo/new", sim.Folders);
        Assert.Empty(sim.Files);
    }

    [Fact]
    public async Task Rename_LongestPathsFitOneFrame()
    {
        var (sim, device) = await ConnectAsync();
        var from = "E:/amiibo/" + new string('a', 40) + ".bin";
        var to = "E:/amiibo/" + new string('b', 40) + ".bin";
        sim.AddFile(from, [9]);

        await device.RenameAsync(from, to);

        Assert.Equal(new byte[] { 9 }, sim.Files[to]);
        Assert.Equal(0, sim.SplitRequests);
    }

    [Fact]
    public async Task Requests_ThatDontFitAFrameAreRefused()
    {
        // The firmware handles each frame on its own, so a request split across frames would be misread.
        var (sim, device) = await ConnectAsync(mtu: 40);
        var count = sim.Received.Count;

        await Assert.ThrowsAsync<BleException>(() => device.RenameAsync("E:/amiibo/" + new string('a', 40), "E:/amiibo/b"));
        Assert.Equal(count, sim.Received.Count);
    }

    [Fact]
    public async Task Errors_BecomePixlExceptions()
    {
        var (sim, device) = await ConnectAsync();

        var missing = await Assert.ThrowsAsync<PixlException>(() => device.ReadFileAsync("E:/nope.bin"));
        Assert.Equal(PixlDevice.CmdOpenFile, missing.Command);

        sim.Unsupported.Add(PixlDevice.CmdUpdateMeta);
        var old = await Assert.ThrowsAsync<PixlException>(() => device.UpdateMetaAsync("E:/x", PixlFileMeta.Empty));
        Assert.Equal(0xFF, old.Status);
        Assert.Contains("firmware", old.Message);
    }

    [Fact]
    public async Task Paths_PastTheLimitAreRefusedBeforeSending()
    {
        var (sim, device) = await ConnectAsync();
        var count = sim.Received.Count;

        await Assert.ThrowsAsync<BleException>(() => device.CreateFolderAsync("E:/" + new string('x', 70)));
        Assert.Equal(count, sim.Received.Count);
    }

    [Fact]
    public async Task UploadTag_WritesA540ByteBinIntoTheAmiiboFolder()
    {
        var (sim, device) = await ConnectAsync();
        var dump = TestData.Amiibo();

        await device.UploadTagAsync("Mario", dump);

        Assert.Equal(dump.ToArray(TagDump.DataLength), sim.Files["E:/amiibo/Mario.bin"]);
    }

    [Fact]
    public async Task Format_ClearsTheDrive()
    {
        var (sim, device) = await ConnectAsync();
        sim.AddFile("E:/amiibo/a.bin", [1]);

        await device.FormatDriveAsync('E');

        Assert.Empty(sim.Files);
    }

    [Fact]
    public void Meta_RoundTripsAndToleratesASizePrefix()
    {
        var meta = new PixlFileMeta("Café", true, "0000000000000002");
        var encoded = PixlDevice.EncodeMeta(meta);

        Assert.Equal(meta, PixlDevice.ParseMeta(encoded));
        Assert.Equal(meta, PixlDevice.ParseMeta([(byte)encoded.Length, .. encoded]));
        Assert.Equal(PixlFileMeta.Empty, PixlDevice.ParseMeta([]));
        Assert.Equal(PixlFileMeta.Empty, PixlDevice.ParseMeta([0xFF]));
        Assert.Throws<BleException>(() => PixlDevice.EncodeMeta(new PixlFileMeta(new string('x', 91))));
    }

    [Theory]
    [InlineData("E:/amiibo", "E:/")]
    [InlineData("E:/amiibo/fav", "E:/amiibo")]
    [InlineData("E:/amiibo/", "E:/")]
    [InlineData("E:/", null)]
    public void Path_Parent(string path, string? parent) => Assert.Equal(parent, PixlPath.Parent(path));

    [Fact]
    public void Path_Validation()
    {
        Assert.Null(PixlPath.ValidateName("link.bin", "E:/amiibo"));
        Assert.NotNull(PixlPath.ValidateName("a/b", "E:/"));
        Assert.NotNull(PixlPath.ValidateName(new string('x', 48), "E:/"));
        Assert.NotNull(PixlPath.ValidateName(new string('x', 40), "E:/amiibo/a-long-folder-name"));
        Assert.Equal("E:/a", PixlPath.Combine("E:/", "a"));
        Assert.Equal("E:/a/b", PixlPath.Combine("E:/a", "b"));
    }

    // ---------------------------------------------------------------- Detection

    private static TagDeviceDetector Detector() => new() { EspruinoProbeTimeout = TimeSpan.FromMilliseconds(500), AmiiboLinkProbeTimeout = TimeSpan.FromMilliseconds(500) };

    private sealed class Adapter(IBleConnection connection) : IBleAdapter
    {
        public bool IsAvailable => true;
        public bool CanScan => false;
        public bool CanRequestDevice => false;
#pragma warning disable CS0067
        public event EventHandler<BleDeviceInfo>? DeviceDiscovered;
#pragma warning restore CS0067
        public Task<bool> CheckAvailabilityAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task StartScanAsync(BleScanFilter filter, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopScanAsync() => Task.CompletedTask;
        public Task<BleDeviceInfo?> RequestDeviceAsync(BleScanFilter filter, CancellationToken cancellationToken = default) => Task.FromResult<BleDeviceInfo?>(null);
        public Task<IReadOnlyList<BleDeviceInfo>> GetKnownDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BleDeviceInfo>>([]);
        public Task<IBleConnection> ConnectAsync(BleDeviceInfo device, CancellationToken cancellationToken = default) => Task.FromResult(connection);
    }

    [Fact]
    public void GuessKind_RecognisesThePixlName()
    {
        Assert.Equal(TagDeviceKind.Pixl, TagDeviceDetector.GuessKind(new BleDeviceInfo("1", "Pixl.js", [])));
        Assert.True(BleUuids.TagDeviceFilter.Matches(new BleDeviceInfo("1", "Pixl.js", [])));
    }

    [Fact]
    public async Task Detection_FindsAnUnnamedPixlWithoutSendingPuckTextOrTheAmiLoopHandshake()
    {
        var sim = new PixlSimulator(name: "");
        var device = await Detector().ConnectAsync(new Adapter(sim), sim.Device);

        Assert.IsType<PixlDevice>(device);

        // Nothing typed for a puck console, and never the AmiLoop handshake, whose 02 means "enter DFU" to a Pixl.js.
        Assert.DoesNotContain(sim.Received, w => Encoding.ASCII.GetString(w).Contains("fastMode"));
        Assert.DoesNotContain(sim.Received, w => w[0] == 0x02);
        Assert.False(sim.EnteredDfu);
    }

    [Fact]
    public async Task Detection_IdentifiesAPuckThroughItsConsole()
    {
        var sim = new EspruinoPuckSimulator(name: "");
        var device = await Detector().ConnectAsync(new Adapter(sim), sim.Device);

        Assert.IsType<PuckDevice>(device);

        // Asked whether fastMode exists, without calling into the Flask app's tag.getList.
        var probe = Assert.Single(sim.Expressions);
        Assert.Contains("typeof fastMode", probe);
        Assert.DoesNotContain("tag.getList()", probe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detection_IdentifiesAFlaskOrSlideByTagGetList(bool slide)
    {
        var sim = new FlaskSimulator(slide);
        var device = Assert.IsType<FlaskDevice>(await Detector().ConnectAsync(new Adapter(sim), sim.Device));

        Assert.Equal(slide ? TagDeviceKind.Slide : TagDeviceKind.Flask, device.Kind);
        Assert.Equal(slide ? "6.5" : "8.2", device.ScriptVersion);
        Assert.Contains("typeof tag.getList", sim.Expressions[0]);
    }

    [Fact]
    public async Task Flask_ListsUploadsAndDownloadsTags()
    {
        var sim = new FlaskSimulator();
        var device = Assert.IsType<FlaskDevice>(await Detector().ConnectAsync(new Adapter(sim), sim.Device));

        var tags = await device.GetTagsAsync();
        Assert.Equal("Mario", Assert.Single(tags).DisplayName);
        Assert.Equal("0e5c0502", tags[0].AmiiboTail);

        var dump = TestData.Amiibo();
        await device.UploadTagsAsync([("Link", dump)]);
        var added = (await device.GetTagsAsync())[1];
        Assert.Equal("Link", added.DisplayName);
        Assert.Equal(dump.ToArray(TagDump.DataLength), sim.Uploaded[added.Name]);
        Assert.Equal(dump.ToArray(TagDump.DataLength), (await device.DownloadTagAsync(added)).ToArray(TagDump.DataLength));

        var selected = await device.SelectTagAsync(added);
        Assert.Equal(added.Name, selected?.Name);

        await device.DeleteTagAsync(added);
        Assert.Single(sim.Tags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detection_IdentifiesNextGenFlaskFirmwareByItsVersion(bool slide)
    {
        var sim = new FlaskNextGenSimulator(slide);
        var device = Assert.IsType<FlaskNextGenDevice>(await Detector().ConnectAsync(new Adapter(sim), sim.Device));

        Assert.Equal(slide ? TagDeviceKind.Slide : TagDeviceKind.Flask, device.Kind);
        Assert.Equal("10", device.ScriptVersion);
        Assert.Equal("FO1.0", device.FirmwareBuild);
        Assert.True(device.CanUploadKeys);
        Assert.Equal(39, device.Memory?.TotalPages);

        // The console probe was rejected, then the version asked for, as flask.run does.
        Assert.Contains(sim.Commands, c => c.Contains("typeof tag.getList"));
        Assert.Contains("screen.getVersion()", sim.Commands);
    }

    [Fact]
    public async Task NextGenFlask_ManagesTagsFoldersAndKeysWithItsOwnCommands()
    {
        var sim = new FlaskNextGenSimulator();
        var device = Assert.IsType<FlaskNextGenDevice>(await Detector().ConnectAsync(new Adapter(sim), sim.Device));
        Assert.False(((ITagListDevice)device).CanReorderTags);

        var tags = await device.GetTagsAsync();
        Assert.Equal(2, tags.Count);
        Assert.Equal("0e5c0502", tags[0].AmiiboTail);
        Assert.Equal("1k", tags[1].TagType);
        Assert.False(tags[1].IsNtag215);
        Assert.Equal(2, tags[1].FolderId);
        Assert.Equal(["Cards", "Spare"], device.Folders.Select(f => f.Name));

        // Uploads go into a folder, with the longer next-gen names; later writes are up to 200 bytes.
        var dump = TestData.Amiibo();
        sim.Received.Clear();
        await device.UploadAsync([("Link", dump.ToArray(TagDump.DataLength))], folderId: 2);
        Assert.Contains(sim.Received, w => w.Length > 20 && w.Length <= 200);
        var added = (await device.GetTagsAsync()).Single(t => t.DisplayName == "Link");
        Assert.Equal(2, added.FolderId);
        Assert.Contains(sim.Commands, c => c.StartsWith($"tag.uploadWithMeta(\"{added.Name}\",1,1,\"", StringComparison.Ordinal) && c.EndsWith("\",2,0,0)", StringComparison.Ordinal));
        Assert.Equal(dump.ToArray(TagDump.DataLength), (await device.DownloadTagAsync(added)).ToArray(TagDump.DataLength));

        await device.SelectTagAsync(added);
        Assert.Equal(added.Id, sim.CurrentId);

        await device.RenameTagAsync(added, "Hero");
        var renamed = (await device.GetTagsAsync()).Single(t => t.Id == added.Id);
        Assert.Equal("Hero", renamed.DisplayName);

        await device.SetTagImageAsync(renamed, 37);
        Assert.Contains($"tag.setImage({renamed.Index}, \"37\")", sim.Commands);
        Assert.Equal("37", (await device.GetTagsAsync()).Single(t => t.Id == added.Id).Image);

        await device.CreateFolderAsync("Games");
        await device.GetTagsAsync();
        var games = device.Folders.Single(f => f.Name == "Games");
        await device.MoveFolderAsync(games, null);
        Assert.Equal("Games", sim.Folders[0].Name);
        await device.DeleteFolderAsync(games);
        Assert.DoesNotContain(sim.Folders, f => f.Name == "Games");

        await device.DeleteTagsAsync([tags[1], renamed]);
        Assert.Equal("Mario|3zfkle|0", Assert.Single(sim.Tags).Name);

        await device.UploadKeysAsync([1, 2, 3]);
        Assert.Equal("AQID", sim.Keys);

        // Every command was one the firmware knows.
        var start = sim.Commands.IndexOf("screen.getVersion()");
        Assert.DoesNotContain(sim.Commands.Skip(start), c => c.Contains("typeof", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NextGenFlask_ReportsRejectedCommandsAsErrors()
    {
        var sim = new FlaskNextGenSimulator();
        var device = Assert.IsType<FlaskNextGenDevice>(await Detector().ConnectAsync(new Adapter(sim), sim.Device));
        var tag = (await device.GetTagsAsync())[0];

        // The firmware can't reorder tags, and answers what it doesn't know with ERR.
        await Assert.ThrowsAsync<NotSupportedException>(() => device.MoveTagAsync(tag, 1));
        var error = await Assert.ThrowsAsync<DeviceCommandException>(() => device.DownloadDataAsync(tag with { Name = "Gone|0|0" }));
        Assert.StartsWith("ERR:", error.Message);

        // And keeps working afterwards.
        Assert.Equal(2, (await device.GetTagsAsync()).Count);
    }

    [Fact]
    public async Task Detection_FindsAnUnnamedAmiiboLinkByItsInfoReply()
    {
        var sim = new AmiiboLinkSimulator(name: "");
        var device = await Detector().ConnectAsync(new Adapter(sim), sim.Device);

        Assert.IsType<AmiiboLinkDevice>(device);
        Assert.DoesNotContain(sim.Received, w => Encoding.ASCII.GetString(w).Contains("fastMode"));
    }

    [Fact]
    public async Task Detection_TrustsTheAmiiboLinkNameAndOnlyAsksItsVersion()
    {
        var sim = new AmiiboLinkSimulator(name: "amiibolink");
        var device = Assert.IsType<AmiiboLinkDevice>(await Detector().ConnectAsync(new Adapter(sim), sim.Device));

        // No other device's probe, just get version (plain, which both protocols take).
        Assert.Equal(new byte[] { 0xA2, 0xB2 }, Assert.Single(sim.Received));
        Assert.Equal(AmiiboLinkProtocol.V2, device.Protocol);
        Assert.Equal("1.2.1.33 (V2)", device.Firmware);
    }

    [Theory]
    [InlineData(AmiiboLinkProtocol.V1)]
    [InlineData(AmiiboLinkProtocol.V2)]
    public async Task AmiiboLink_WritesATagInEitherProtocol(AmiiboLinkProtocol protocol)
    {
        var sim = new AmiiboLinkSimulator(protocol, name: "amiibolink");
        var device = Assert.IsType<AmiiboLinkDevice>(await Detector().ConnectAsync(new Adapter(sim), sim.Device));
        Assert.Equal(protocol, device.Protocol);
        var dump = TestData.Amiibo();

        await device.WriteTagAsync(dump);

        Assert.Equal(dump.ToArray(TagDump.DataLength), sim.AppliedTag);
        Assert.Equal(0, sim.Garbled);

        // V1 sends the official app's 150 byte pieces; V2 has to fit 32 byte packets.
        var dataPackets = sim.Commands.Where(c => c[0] == 0xDD && c[1] == 0xAA).ToList();
        Assert.Equal(protocol == AmiiboLinkProtocol.V1 ? 4 : 27, dataPackets.Count);
    }

    [Fact]
    public void AmiiboLink_DataPacketsAreNumberedFromZero()
    {
        var packets = AmiiboLinkDevice.BuildWritePackets(new byte[540], 150);
        var data = packets.Where(p => p[0] == 0xDD).ToList();

        Assert.Equal(new byte[] { 0xDD, 0xAA, 0x00, 0x96 }, data[0][..4]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, data[0][^2..]);
        Assert.Equal(new byte[] { 0x00, 0x03 }, data[3][^2..]);
        Assert.Equal(0x5A, data[3][3]);
        Assert.Equal(new byte[] { 0xAC, 0xAC, 0x00, 0x04, 0x00, 0x00, 0x02, 0x1C }, packets[1]);
    }

    /// <summary>
    /// Reports synchronously (Progress&lt;T&gt; posts to the thread pool, which would make assertions racy).
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
