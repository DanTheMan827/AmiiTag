using AmiiTag.Simulation;
using AmiiTag.Tags;

namespace AmiiTag.Tests;

public class V3SimulatorLifecycleTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int milliseconds) => _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }

    private static (NtagI2cPlusSimulator Tag, Clock Clock) Create()
    {
        var clock = new Clock();
        var raw = NtagI2cPlusSimulator.CreateBlank().SectorMemory(0)
            .Concat(new byte[1024]).ToArray();
        var tag = new NtagI2cPlusSimulator(raw, new byte[] { 2, 3, 4 }, clock);
        return (tag, clock);
    }

    private static byte[] Request => [0xA6, 0xF0, 0xFF, .. LockOnResponse.Normalize([1])];
    private static byte Status(NtagI2cPlusSimulator tag) => tag.Transceive([0x3A, 0xED, 0xED]).Data[2];

    [Fact]
    public void TimeAndPollingAloneNeverPublishSram()
    {
        var (tag, clock) = Create();
        clock.Advance(10000);
        for (var i = 0; i < 100; i++) Assert.Equal(0, Status(tag) & 8);
        Assert.Equal(TagResponseKind.Nak, tag.Transceive([0x3A, 0xF0, 0xFF]).Kind);
        Assert.Equal(TagResponseKind.None, tag.Transceive([0x60]).Kind);
    }

    [Fact]
    public void RequestStartsDelayAndLastPageConsumesResponse()
    {
        var (tag, clock) = Create();
        var expected = (byte[])tag.Sram.Clone();
        Assert.Equal(TagResponseKind.Ack, tag.Transceive(Request).Kind);
        Assert.Equal(0x50, Status(tag) & 0x58); // I²C locked and request ready for I²C, RF not ready.
        clock.Advance(9);
        Assert.Equal(0, Status(tag) & 8);
        clock.Advance(1);
        Assert.Equal(8, Status(tag) & 0x58);
        Assert.Equal(expected[..32], tag.Transceive([0x3A, 0xF0, 0xF7]).Data);
        Assert.True(tag.IsSramResponseReady);
        Assert.Equal(expected[32..], tag.Transceive([0x3A, 0xF8, 0xFF]).Data);
        clock.Advance(10000);
        Assert.Equal(0, Status(tag) & 8);
        Assert.Equal(expected, tag.Sram);
        Assert.Equal(TagResponseKind.Ack, tag.Transceive(Request).Kind);
        clock.Advance(10);
        Assert.Equal(expected, tag.Transceive([0x3A, 0xF0, 0xFF]).Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BusyAccessCanNakOrStaySilent_ReselectDoesNotRestartProcessing(bool silent)
    {
        var (tag, clock) = Create();
        tag.SilentWhileProcessing = silent;
        tag.Transceive(Request);
        Assert.Equal(silent ? TagResponseKind.None : TagResponseKind.Nak, tag.Transceive([0x30, 0]).Kind);
        Assert.Equal(!silent, tag.IsHalted);
        tag.Activate();
        Assert.Equal(0, Status(tag) & 8);
        clock.Advance(10);
        Assert.Equal(tag.Sram, tag.Transceive([0x3A, 0xF0, 0xFF]).Data);
    }

    [Fact]
    public void PowerCycleDiscardsPendingAndReadyResponses()
    {
        var (tag, clock) = Create();
        tag.Transceive(Request);
        tag.PowerCycle();
        clock.Advance(10);
        Assert.Equal(0, Status(tag) & 8);
        Assert.Null(tag.LastSramRequest);
        tag.Transceive(Request);
        clock.Advance(10);
        Assert.True(tag.IsSramResponseReady);
        tag.PowerCycle();
        Assert.False(tag.IsSramResponseReady);
    }

    [Fact]
    public void InvalidRequestIsTransportAckedButNeverPublishesResponse()
    {
        var (tag, clock) = Create();
        var invalid = Request;
        invalid[^1] ^= 1;
        Assert.Equal(TagResponseKind.Ack, tag.Transceive(invalid).Kind);
        clock.Advance(10000);
        Assert.Equal(0, Status(tag) & 8);
    }

    [Fact]
    public void PageWritesRequireCompleteRequestThroughFf()
    {
        var (tag, clock) = Create();
        var payload = Request[3..];
        Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xA2, 0xFF, .. payload[60..]]).Kind);
        clock.Advance(100);
        Assert.False(tag.IsSramResponseReady);
        for (var page = 0; page < 16; page++)
            Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xA2, (byte)(0xF0 + page), .. payload[(page * 4)..(page * 4 + 4)]]).Kind);
        Assert.Equal(payload, tag.LastSramRequest);
        Assert.False(tag.IsSramResponseReady);
        clock.Advance(10);
        Assert.Equal(tag.Sram, tag.Transceive([0x3A, 0xF0, 0xFF]).Data);
    }

    [Fact]
    public void DroppedAckStillProcessesRequest_DroppedReadCanBeRetried()
    {
        var (tag, clock) = Create();
        tag.DropRequestAcknowledgements = 1;
        tag.DropSramReadResponses = 1;
        Assert.Equal(TagResponseKind.None, tag.Transceive(Request).Kind);
        clock.Advance(10);
        Assert.Equal(8, Status(tag) & 8);
        Assert.Equal(TagResponseKind.None, tag.Transceive([0x3A, 0xF0, 0xFF]).Kind);
        Assert.True(tag.IsSramResponseReady);
        Assert.Equal(tag.Sram, tag.Transceive([0x3A, 0xF0, 0xFF]).Data);
        Assert.False(tag.IsSramResponseReady);
    }

    [Fact]
    public void CapturedReadySessionDoesNotPublishAResponseAtStartup()
    {
        var (source, clock) = Create();
        var raw = source.SectorMemory(0).Concat(source.SectorMemory(1)).ToArray();
        raw[0xEC * 4] = 0x40; // Dump captured with I²C-to-RF direction and RF_READY set.
        raw[0xED * 4 + 2] = 0x29;
        var tag = new NtagI2cPlusSimulator(raw, new byte[] { 2, 3, 4 }, clock);
        var session = tag.Transceive([0x3A, 0xEC, 0xED]).Data;
        Assert.Equal(0x41, session[0]);
        Assert.Equal(0x21, session[6]);
        tag.Transceive(Request);
        clock.Advance(10);
        Assert.Equal(0x40, tag.Transceive([0x3A, 0xEC, 0xEC]).Data[0]);
        Assert.Equal(tag.Sram, tag.Transceive([0x3A, 0xF0, 0xFF]).Data);
        Assert.Equal(0x41, tag.Transceive([0x3A, 0xEC, 0xEC]).Data[0]);
    }

    [Fact]
    public void SramProtectionRequiresAuthenticationForRequestsAndResponses()
    {
        var (source, clock) = Create();
        var raw = source.SectorMemory(0).Concat(source.SectorMemory(1)).ToArray();
        raw[0xE7 * 4] = 0x04; // SRAM_PROT; blank tag password is zero.
        var tag = new NtagI2cPlusSimulator(raw, new byte[] { 2, 3, 4 }, clock);
        Assert.Equal(TagResponseKind.Nak, tag.Transceive(Request).Kind);
        tag.Activate();
        Assert.Equal(TagResponseKind.Data, tag.Transceive([0x1B, 0, 0, 0, 0]).Kind);
        Assert.Equal(TagResponseKind.Ack, tag.Transceive(Request).Kind);
        clock.Advance(10);
        tag.Activate(); // RF selection drops authentication, not the completed I²C response.
        Assert.Equal(TagResponseKind.Nak, tag.Transceive([0x3A, 0xF0, 0xFF]).Kind);
        tag.Activate();
        tag.Transceive([0x1B, 0, 0, 0, 0]);
        Assert.Equal(tag.Sram, tag.Transceive([0x3A, 0xF0, 0xFF]).Data);
    }

    [Fact]
    public void SectorSelectPassiveAckAndHaltStaySilent()
    {
        var (tag, _) = Create();
        Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xC2, 0xFF]).Kind);
        Assert.Equal(TagResponseKind.None, tag.Transceive([1, 0, 0, 0]).Kind);
        Assert.False(tag.IsHalted);
        Assert.Equal(1, tag.CurrentSector);
        Assert.Equal(TagResponseKind.None, tag.Transceive([0x50, 0]).Kind);
        Assert.Equal(TagResponseKind.None, tag.Transceive([0x60]).Kind);
    }
}
