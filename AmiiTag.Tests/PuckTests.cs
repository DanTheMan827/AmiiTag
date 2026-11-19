using System.Text;
using AmiiTag.Bluetooth;
using AmiiTag.Bluetooth.Devices;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

public class PuckTests
{
    [Fact]
    public async Task Legacy_SelectHasNoReply_AndRefreshReadsOnlyOneSlot()
    {
        var sim = new LegacyFirmware();
        var device = new LegacyPuckDevice(sim);
        Assert.True(await device.InitializeAsync());
        var reads = sim.Reads;
        await device.SelectSlotAsync(2);
        Assert.Equal(reads, sim.Reads);
        Assert.Equal(new SlotSummary(2, 3), await device.GetSlotSummaryAsync());
        ISlotTagDevice slots = device;
        Assert.Equal(2, (await slots.GetSlotInformationAsync(2)).Slot);
        Assert.Equal(new byte[] { 1, 2 }, sim.Commands[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedOrDisabledAmiitool_NeverRequestsKeys(bool fragmented)
    {
        var sim = new FirmwarePuck { Fragmented = fragmented };
        var callbacks = 0;
        var device = new PuckDevice(sim, () => { callbacks++; return new byte[160]; });
        Assert.True(await device.InitializeAsync());
        Assert.Equal("dtm-2.3.0", device.Firmware);
        Assert.Equal("Test Puck", device.Name);
        Assert.False(device.AmiitoolEnabled);
        Assert.Equal(0, callbacks);
        var before = sim.Received.Count;
        await Assert.ThrowsAsync<BleException>(() => device.UploadKeysAsync(new byte[160]));
        Assert.Equal(before, sim.Received.Count);
        Assert.Equal(new SlotSummary(0, 3), await device.GetSlotSummaryAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvertisedAmiitool_UploadsKeysAndRefreshesOnlyChangedSlot(bool fragmented)
    {
        var sim = new FirmwarePuck { Capabilities = [0, 1, 2], Fragmented = fragmented };
        var keys = Enumerable.Range(0, 160).Select(i => (byte)i).ToArray();
        var device = new PuckDevice(sim, () => keys);
        Assert.True(await device.InitializeAsync());
        Assert.True(device.CanRandomizeUid);
        Assert.Equal(keys, sim.UploadedKeys);
        sim.Received.Clear();
        await device.RandomizeUidAsync(1);
        ISlotTagDevice slots = device;
        Assert.Equal(1, (await slots.GetSlotInformationAsync(1)).Slot);
        Assert.Equal(2, sim.Received.Count);
        Assert.Equal(new byte[] { 0xEE, 2, 1 }, sim.Received[0]);
        Assert.Equal(new byte[] { 1, 1, 1 }, sim.Received[1]);
    }

    [Fact]
    public async Task LoadedKeys_AreNotUploadedAgain()
    {
        var sim = new FirmwarePuck { Capabilities = [0, 1, 2], KeysLoaded = true };
        var device = new PuckDevice(sim, () => throw new Exception("Keys must not be requested"));
        Assert.True(await device.InitializeAsync());
        Assert.True(device.CanRandomizeUid);
        Assert.Null(sim.UploadedKeys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SubcommandCapabilities_AreRespected(int variant)
    {
        var sim = new FirmwarePuck { Capabilities = variant == 0 ? [0] : [1, 2] };
        var callbacks = 0;
        var device = new PuckDevice(sim, () => { callbacks++; return new byte[160]; });
        Assert.True(await device.InitializeAsync());
        Assert.True(device.AmiitoolEnabled);
        Assert.False(device.CanRandomizeUid);
        Assert.Equal(variant == 0, device.CanUploadKeys);
        Assert.Equal(variant == 0 ? 1 : 0, callbacks);
        var before = sim.Received.Count;
        await Assert.ThrowsAsync<BleException>(() => device.RandomizeUidAsync(0));
        Assert.Equal(before, sim.Received.Count);
    }

    [Theory]
    [InlineData(new byte[] { 0xEE })]
    [InlineData(new byte[] { 0, 0 })]
    [InlineData(new byte[] { 0, 1, 2, 3 })]
    public async Task InvalidCapabilities_DoNotEnableUploads(byte[] response)
    {
        var sim = new FirmwarePuck { Capabilities = response };
        var device = new PuckDevice(sim, () => throw new Exception("Unsupported key request"));
        Assert.True(await device.InitializeAsync());
        Assert.False(device.AmiitoolEnabled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WriteThenSummary_ConsumesAllFirmwareAcknowledgements(bool amiitool, bool fragmented)
    {
        var sim = new FirmwarePuck { Capabilities = amiitool ? [0, 1, 2] : null, Fragmented = fragmented, KeysLoaded = true };
        var device = new PuckDevice(sim);
        Assert.True(await device.InitializeAsync());
        var data = Enumerable.Range(0, TagDump.FullLength).Select(i => (byte)(i * 7)).ToArray();
        await device.WriteTagAsync(new TagDump(data), 2);
        Assert.Equal(data, sim.WrittenTag);
        Assert.Equal(new SlotSummary(2, 3), await device.GetSlotSummaryAsync());
        Assert.Equal(3, (await device.GetSlotsAsync()).Count);
        await device.SelectSlotAsync(1);
        Assert.Equal(new SlotSummary(1, 3), await device.GetSlotSummaryAsync());
    }

    [Fact]
    public async Task Summary_ToleratesLateSelectionAcknowledgement()
    {
        var sim = new FirmwarePuck { LateSelectionAck = true, Fragmented = true };
        var device = new PuckDevice(sim);
        Assert.True(await device.InitializeAsync());
        Assert.Equal(new SlotSummary(0, 3), await device.GetSlotSummaryAsync());
    }

    [Theory]
    [InlineData("AMIIBOLINK", TagDeviceKind.AmiiboLink)]
    [InlineData("amiloop", TagDeviceKind.AmiLoop)]
    [InlineData("amiibolink Puck", TagDeviceKind.Unknown)]
    [InlineData("my amiloop", TagDeviceKind.Unknown)]
    [InlineData("AmiLoop2", TagDeviceKind.Unknown)]
    public void DeviceNames_RequireExactMatch(string name, TagDeviceKind expected)
    {
        Assert.Equal(expected, TagDeviceDetector.GuessKind(new BleDeviceInfo("test", name, [])));
        Assert.DoesNotContain(BleUuids.TagDeviceFilter.NamePrefixes, p =>
            p.Equals("amiibolink", StringComparison.OrdinalIgnoreCase) || p.Equals("amiloop", StringComparison.OrdinalIgnoreCase));
    }

    // Wire responses from the linked master/amiitool ntag215.js builds. Notifications
    // deliberately split headers and bodies, or combine multiple logical writes.
    private sealed class FirmwarePuck : SimulatedUartDevice
    {
        public FirmwarePuck() : base("Puck.js test") { }
        public byte[]? Capabilities { get; init; }
        public bool Fragmented { get; init; }
        public bool KeysLoaded { get; set; }
        public bool LateSelectionAck { get; init; }
        public byte[]? UploadedKeys { get; private set; }
        public byte[]? WrittenTag { get; private set; }
        private int _remaining;
        private bool _keys;
        private byte _slot;
        private byte _current;
        private readonly List<byte> _upload = [];

        private void Reply(byte[] bytes)
        {
            var size = Fragmented ? 7 : 244;
            for (var i = 0; i < bytes.Length; i += size) Notify(bytes[i..Math.Min(i + size, bytes.Length)]);
        }

        protected override void OnWrite(byte[] data)
        {
            if (_remaining > 0)
            {
                _upload.AddRange(data);
                _remaining -= data.Length;
                if (_remaining != 0) return;
                if (_keys) { UploadedKeys = _upload.ToArray(); KeysLoaded = true; Reply([0]); }
                else { WrittenTag = _upload.ToArray(); Reply([5, _slot, 0x12, 0x34, 0x56, 0x78]); }
                return;
            }
            if (Encoding.ASCII.GetString(data).Contains("fastMode()")) { Reply(Encoding.ASCII.GetBytes("DTM_PUCK_FAST")); return; }
            switch (data[0])
            {
                case 0xFC: Reply(Encoding.ASCII.GetBytes("dtm-2.3.0")); Reply([0]); break;
                case 0xFA: Reply(Encoding.ASCII.GetBytes("Test Puck")); Reply([0]); break;
                case 0xEE:
                    if (Capabilities == null) { Reply(Encoding.ASCII.GetBytes("Bad Command")); break; }
                    if (data.Length == 1) Reply(Capabilities);
                    else if (data[1] == 0 && data.Length == 2) Reply([KeysLoaded ? (byte)0 : (byte)1]);
                    else if (data[1] == 0) { _remaining = 160; _keys = true; _upload.Clear(); Reply([0xEE, 0, 0]); }
                    else if (data[1] == 2) Reply([0xEE, 2, data[2], 4, 1, 2, 3, 4, 5, 6, 7, 8]);
                    break;
                case 1:
                    if (data.Length == 1) Reply(LateSelectionAck ? [0xFF, 0, 1, _current, 3] : [1, _current, 3]);
                    else
                    {
                        var slots = new List<byte>();
                        for (var i = 0; i < data[2]; i++) { slots.AddRange([1, (byte)(data[1] + i)]); slots.AddRange(new byte[80]); }
                        Reply(slots.ToArray());
                    }
                    break;
                case 5: _remaining = TagDump.FullLength; _keys = false; _slot = data[1]; _upload.Clear(); Reply(data); break;
                case 4: Reply(data); break;
                case 0xFF: _current = data[1]; Reply(data); break;
                default: throw new InvalidOperationException($"Unexpected command {Convert.ToHexString(data)}");
            }
        }
    }

    private sealed class LegacyFirmware : IBleConnection, IBleCharacteristic
    {
        public BleDeviceInfo Device { get; } = new("legacy", "Puck.js", [BleUuids.LegacyPuckService]);
        public bool IsConnected => true;
        public Guid Id => BleUuids.LegacyPuckCommand;
        public List<byte[]> Commands { get; } = [];
        public int Reads { get; private set; }
        private byte[] _response = [];
        private byte _current;
#pragma warning disable CS0067
        public event EventHandler? Disconnected;
        public event EventHandler<byte[]>? ValueUpdated;
#pragma warning restore CS0067
        public Task<bool> HasServiceAsync(Guid service, CancellationToken cancellationToken = default) => Task.FromResult(service == BleUuids.LegacyPuckService);
        public Task<IBleCharacteristic?> GetCharacteristicAsync(Guid service, Guid characteristic, CancellationToken cancellationToken = default) =>
            Task.FromResult<IBleCharacteristic?>(service == BleUuids.LegacyPuckService ? this : null);
        public Task<int> RequestMtuAsync(int mtu, CancellationToken cancellationToken = default) => Task.FromResult(247);
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task StartNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<byte[]> ReadAsync(CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(_response); }
        public Task WriteAsync(byte[] data, bool withResponse = true, CancellationToken cancellationToken = default)
        {
            Commands.Add(data);
            if (data[0] == 0xFF) _current = data[1]; // No response update on legacy firmware.
            else if (data[0] == 1 && data.Length == 1) _response = [1, _current, 3];
            else if (data[0] == 1) _response = [1, data[1], .. new byte[80]];
            else throw new InvalidOperationException("Unexpected legacy command");
            return Task.CompletedTask;
        }
    }
}
