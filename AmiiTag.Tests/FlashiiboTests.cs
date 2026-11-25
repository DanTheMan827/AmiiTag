using AmiiTag.Bluetooth;
using AmiiTag.Bluetooth.Devices;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

public sealed class FlashiiboTests
{
    [Theory]
    [InlineData("Flashiibo Pro")]
    [InlineData("Flashiibo Fob")]
    [InlineData("Flashbo")]
    [InlineData("My Flashiibo Fob")]
    public void DiscoveryTreatsFlashiiboNamesAsProtocolAmbiguous(string name)
    {
        var info = new BleDeviceInfo("test", name, []);
        Assert.Equal(TagDeviceKind.Unknown, TagDeviceDetector.GuessKind(info));
        Assert.True(BleUuids.TagDeviceFilter.Matches(info));
    }

    [Fact]
    public async Task FactoryCreatesFlashiiboProDriver()
    {
        var sim = new PixlSimulator("Flashiibo Pro") { Version = "26.11.13" };
        var device = await new TagDeviceDetector().ConnectAsync(new Adapter(sim), sim.Device);

        var flashiibo = Assert.IsType<FlashiiboDevice>(device);
        Assert.Equal(TagDeviceKind.Flashiibo, flashiibo.Kind);
        Assert.Equal("26.11.13", flashiibo.Firmware);
    }

    [Fact]
    public async Task ProDriveUsageTreatsSecondCapacityAsUsedBytes()
    {
        var sim = new PixlSimulator("Flashiibo Pro")
        {
            DriveSize = 10_000,
            ReportUsedCapacity = true,
        };
        sim.AddFile("E:/amiibo/test.bin", new byte[1_500]);
        var device = new FlashiiboDevice(sim);
        Assert.True(await device.InitializeAsync());

        var drive = Assert.Single(await device.GetDrivesAsync());
        Assert.Equal(10_000, drive.TotalBytes);
        Assert.Equal(1_500, drive.UsedBytes);
        Assert.Equal(8_500, drive.FreeBytes);
    }

    [Fact]
    public async Task FactoryCreatesFobDriverAndUsesCompanionCommands()
    {
        var sim = new FobSimulator();
        var device = Assert.IsType<FlashiiboFobDevice>(
            await new TagDeviceDetector().ConnectAsync(new Adapter(sim), sim.Device));

        Assert.Equal("V1.2.3", device.Firmware);
        await device.WriteTagAsync(new TagDump(new byte[TagDump.DataLength]));
        Assert.Equal(5, sim.Received.Count(packet => packet.Length > 2 && packet[2] == 0x87));

        await device.RunCommandAsync(device.Commands.Single(command => command.Id == "automatic"));
        await device.RunCommandAsync(device.Commands.Single(command => command.Id == "reset"));
        Assert.Contains(sim.Received, packet => packet.SequenceEqual(new byte[] { 0x02, 0x02, 0x8A, 0x02, 0x8A, 0x03 }));
        Assert.Contains(sim.Received, packet => packet.SequenceEqual(new byte[] { 0x12, 0x0D, 0x00, 0x02, 0x01, 0x8F, 0x8E, 0x03 }));
    }

    [Fact]
    public void FobBuildsDocumentedUploadPackets()
    {
        var data = Enumerable.Range(0, 540).Select(i => (byte)i).ToArray();
        var packets = FlashiiboFobDevice.BuildPackets(data);

        Assert.Equal(5, packets.Count);
        Assert.Equal(new byte[] { 0x02, 0x83, 0x87, 0x00, 0x00 }, packets[0][..5]);
        Assert.Equal(135, packets[0].Length);
        Assert.Equal(new byte[] { 0x02, 0x1F, 0x87, 0x01, 0x04 }, packets[4][..5]);
        Assert.Equal(35, packets[4].Length);

        foreach (var packet in packets)
        {
            Assert.Equal(0x03, packet[^1]);
            Assert.Equal(packet[^2], packet[1..^2].Aggregate((byte)0, (xor, value) => (byte)(xor ^ value)));
        }
    }

    [Fact]
    public void FobParsesFirmwareLikeCompanionApp()
    {
        var response = new byte[]
        {
            0x02, 0x11, 0x89,
            (byte)'A', (byte)'m', (byte)'i', (byte)'L', (byte)'o', (byte)'o', (byte)'p', (byte)'_',
            (byte)'F', (byte)'W', (byte)'_', (byte)'V', (byte)'1', (byte)'.', (byte)'2', (byte)'.', (byte)'3',
            0x00, 0x03,
        };

        Assert.Equal("V1.2.3", FlashiiboFobDevice.ParseFirmware(response));
        Assert.Null(FlashiiboFobDevice.ParseFirmware([0x02, 0x01, 0x89]));
    }

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

    private sealed class FobSimulator() : SimulatedUartDevice("Flashiibo Fob")
    {
        protected override void OnWrite(byte[] data)
        {
            if (data.SequenceEqual(new byte[] { 0x02, 0x01, 0x89, 0x88, 0x03 }))
            {
                Notify(new byte[]
                {
                    0x02, 0x11, 0x89,
                    (byte)'A', (byte)'m', (byte)'i', (byte)'L', (byte)'o', (byte)'o', (byte)'p', (byte)'_',
                    (byte)'F', (byte)'W', (byte)'_', (byte)'V', (byte)'1', (byte)'.', (byte)'2', (byte)'.', (byte)'3',
                    0x00, 0x03,
                });
            }
            else if (data.SequenceEqual(new byte[] { 0x01, 0x00, 0x00, 0x00 }))
            {
                // The Fob ignores the Pro version probe.
            }
            else if (!data.AsSpan().StartsWith(new byte[] { 0x12, 0x0D, 0x00 }))
            {
                Notify([0x02, 0x01, 0x00, 0x01, 0x03]);
            }
        }
    }
}
