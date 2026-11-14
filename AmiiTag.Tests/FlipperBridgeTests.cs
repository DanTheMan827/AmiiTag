using System.Collections.Concurrent;
using System.Threading.Channels;
using AmiiTag.Nfc.Flipper;
using AmiiTag.Tags;

namespace AmiiTag.Tests;

/// <summary>
/// The host side of the Flipper NFC bridge (protocol v2) against a fake Flipper speaking the same reports.
/// </summary>
public class FlipperBridgeTests
{
    /// <summary>
    /// Plays the Flipper app: HELLO, ACKs, the answer table, relayed frames and a reader mode echo.
    /// </summary>
    private sealed class FakeFlipper : IBridgeTransport
    {
        private readonly Channel<byte[]> _toHost = Channel.CreateUnbounded<byte[]>();
        private readonly BridgeAssembler _assembler = new();
        private readonly Channel<BridgeMessage> _answers = Channel.CreateUnbounded<BridgeMessage>();
        private byte _seq;

        public ConcurrentDictionary<string, BridgeAnswer> Table { get; } = new();

        public List<BridgeMessage> Control { get; } = [];

        public byte[]? ListenParameters { get; private set; }

        public BridgeStatusCode NextStatus { get; set; } = BridgeStatusCode.Ok;

        public void Write(ReadOnlySpan<byte> report)
        {
            Assert.Equal(BridgeProtocol.ReportSize, report.Length);
            if (!_assembler.TryAdd(report, out var message))
            {
                return;
            }

            var p = message.Payload;
            switch (message.Type)
            {
                case BridgeMessageType.HostHello:
                    Send(BridgeMessageType.Hello, 0, [BridgeProtocol.Version, 0, 0x00, 0x04, 0xFD, 0x03, 0, 0, .. "Fake Flipper"u8]);
                    break;
                case BridgeMessageType.GetStatus:
                    Send(BridgeMessageType.Status, 0, [3, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xE8, 0x03, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0]);
                    break;
                case BridgeMessageType.TableSet:
                    var match = p[4..(4 + p[1])];
                    Table[Convert.ToHexString(match)] = BridgeAnswer.Parse([p[0], p[2], p[3], .. p[(4 + p[1])..]]);
                    Send(BridgeMessageType.Ack, 0, [(byte)message.Type, 0]);
                    break;
                case BridgeMessageType.TableClear:
                    Table.Clear();
                    Send(BridgeMessageType.Ack, 0, [(byte)message.Type, 0]);
                    break;
                case BridgeMessageType.Answer:
                    _answers.Writer.TryWrite(message);
                    break;
                case BridgeMessageType.Transceive:
                    // Echoes the frame back reversed, flagging CrcOk when asked to check.
                    var bits = p[2] | (p[3] << 8);
                    var data = p[8..(8 + (bits + 7) / 8)].Reverse().ToArray();
                    byte flags = (p[1] & (byte)BridgeTxFlags.CheckCrc) != 0 ? (byte)BridgeFrameFlags.CrcOk : (byte)0;
                    Send(BridgeMessageType.TransceiveResult, message.Sequence, [0, flags, (byte)bits, (byte)(bits >> 8), 0x2C, 0x01, 0, 0, 0, .. data]);
                    break;
                default:
                    if (message.Type == BridgeMessageType.Listen)
                    {
                        ListenParameters = p;
                    }

                    Control.Add(message);
                    Send(BridgeMessageType.Ack, 0, [(byte)message.Type, (byte)NextStatus]);
                    break;
            }
        }

        public void Send(BridgeMessageType type, byte seq, ReadOnlySpan<byte> payload)
        {
            foreach (var report in BridgeFraming.Encode(type, seq, payload))
            {
                _toHost.Writer.TryWrite(report);
            }
        }

        /// <summary>
        /// A reader frame with a valid CRC (removed by the Flipper); returns the host's answer.
        /// </summary>
        public async Task<BridgeAnswer> ReaderSendsAsync(params byte[] command)
        {
            var seq = ++_seq;
            var frame = new BridgeFrame(seq, 1000u * seq, BridgeFrameFlags.CrcOk, command.Length * 8, command);
            Send(BridgeMessageType.Frame, seq, frame.Encode());
            var response = await _answers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(seq, response.Sequence);
            return BridgeAnswer.Parse(response.Payload);
        }

        /// <summary>
        /// A frame the answer table already answered.
        /// </summary>
        public void TableAnswered(params byte[] command) =>
            Send(BridgeMessageType.Frame, 0, new BridgeFrame(0, 500, BridgeFrameFlags.CrcOk | BridgeFrameFlags.FromTable, command.Length * 8, command).Encode());

        public bool HasAnswerQueued => _answers.Reader.TryPeek(out _);

        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken) => _toHost.Reader.ReadAsync(cancellationToken);

        public void Unplug() => _toHost.Writer.TryComplete(new IOException("Device removed"));

        public ValueTask DisposeAsync()
        {
            _toHost.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private static BridgeFrame Frame(params byte[] data) => new(1, 0, BridgeFrameFlags.CrcOk, data.Length * 8, data);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(58)]
    [InlineData(59)]
    [InlineData(540)]
    [InlineData(BridgeProtocol.MaxPayload)]
    public void Framing_RoundTrips(int length)
    {
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
        var reports = BridgeFraming.Encode(BridgeMessageType.Frame, 42, payload);

        Assert.Equal(Math.Max(1, (length + 57) / 58), reports.Count);
        Assert.All(reports, r => Assert.Equal(64, r.Length));

        var assembler = new BridgeAssembler();
        BridgeMessage message = default;
        for (var i = 0; i < reports.Count; i++)
        {
            Assert.Equal(i == reports.Count - 1, assembler.TryAdd(reports[i], out message));
        }

        Assert.Equal(BridgeMessageType.Frame, message.Type);
        Assert.Equal(42, message.Sequence);
        Assert.Equal(payload, message.Payload);
    }

    [Fact]
    public void Framing_DropsAMessageWithAMissingChunk()
    {
        var reports = BridgeFraming.Encode(BridgeMessageType.Answer, 1, new byte[200]);
        var assembler = new BridgeAssembler();

        Assert.False(assembler.TryAdd(reports[0], out _));
        Assert.False(assembler.TryAdd(reports[2], out _));
        Assert.False(assembler.TryAdd(reports[3], out _));

        var next = BridgeFraming.Encode(BridgeMessageType.Stop, 2, []);
        Assert.True(assembler.TryAdd(next[0], out var message));
        Assert.Equal(BridgeMessageType.Stop, message.Type);
    }

    [Fact]
    public void Answers_EncodeFlagsBitsAndData()
    {
        Assert.Equal(new byte[] { 0x01, 16, 0, 0x80, 0x80 }, BridgeAnswer.Frame([0x80, 0x80]).Encode());
        Assert.Equal(new byte[] { 0x00, 4, 0, 0x0A }, BridgeAnswer.Nibble(TagResponse.AckValue).Encode());
        Assert.Equal(new byte[] { 0x02, 4, 0, 0x00 }, BridgeAnswer.NibbleThenIdle(0).Encode());
        Assert.Equal(new byte[] { 0x00, 0, 0 }, BridgeAnswer.Silence.Encode());

        var custom = new BridgeAnswer(BridgeTxFlags.CustomParity, 16, [0x12, 0x34], [0x02]);
        Assert.Equal(new byte[] { 0x04, 16, 0, 0x12, 0x34, 0x02 }, custom.Encode());
        Assert.Equal(custom.Parity, BridgeAnswer.Parse(custom.Encode()).Parity);
    }

    [Fact]
    public void Frames_ParseParityWhenFlagged()
    {
        var frame = new BridgeFrame(3, 99, BridgeFrameFlags.CrcOk, 24, [1, 2, 3], [0x05]);
        var parsed = BridgeFrame.Parse(new BridgeMessage(BridgeMessageType.Frame, 3, frame.Encode()));

        Assert.Equal(frame.Data, parsed.Data);
        Assert.Equal(new byte[] { 0x05 }, parsed.Parity);
        Assert.True(parsed.IsStandard);
        Assert.False(parsed.FromTable);
    }

    [Fact]
    public void Responder_AnswersLikeAnAmiibo()
    {
        var dump = TestData.Amiibo();
        var responder = new TagSimulatorResponder(Ntag215Simulator.FromAmiiboDump(dump));

        var version = responder.Respond(Frame(0x60));
        Assert.Equal(BridgeTxFlags.AddCrc, version.Flags);
        Assert.Equal(Ntag215Simulator.VersionNtag215, version.Data);

        var read = responder.Respond(Frame(0x30, 0x00));
        Assert.Equal(dump.ToArray(TagDump.DataLength)[..16], read.Data);

        var pack = responder.Respond(Frame([0x1B, .. NtagUid.AmiiboPassword(dump.Uid)]));
        Assert.Equal(new byte[] { 0x80, 0x80 }, pack.Data);
        var nak = responder.Respond(Frame(0x1B, 1, 2, 3, 4));
        Assert.Equal(4, nak.Bits);
        Assert.True(nak.Flags.HasFlag(BridgeTxFlags.Sleep));

        // The Flipper only relays again once the reader reselected the tag.
        Assert.True(responder.Tag.IsHalted);
        Assert.Equal(64, responder.Respond(Frame(0x60)).Bits);
    }

    [Fact]
    public void Responder_SendsAPassiveAckAsSilence()
    {
        var responder = new TagSimulatorResponder(NtagI2cPlusSimulator.CreateBlank());

        var first = responder.Respond(Frame(0xC2, 0xFF));
        Assert.Equal(4, first.Bits);
        Assert.Equal(TagResponse.AckValue, first.Data[0]);
        Assert.True(responder.Respond(Frame(0x01, 0x00, 0x00, 0x00)).IsSilent);
    }

    [Fact]
    public void Responder_IgnoresShortRawFrames()
    {
        var responder = new TagSimulatorResponder(Ntag215Simulator.CreateBlank());
        Assert.True(responder.Respond(new BridgeFrame(1, 0, BridgeFrameFlags.None, 7, [0x40])).IsSilent);
    }

    [Fact]
    public async Task Session_RelaysEveryFrameByDefault()
    {
        var flipper = new FakeFlipper();
        var dump = TestData.Amiibo();
        var tag = Ntag215Simulator.FromAmiiboDump(dump);

        await using var session = await EmulationSession.StartAsync(flipper, tag, "Test amiibo");

        Assert.True(session.IsRunning);
        Assert.False(session.UsesAnswerTable);
        Assert.Empty(flipper.Table);

        var read = await flipper.ReaderSendsAsync(0x30, 0x00);
        Assert.Equal(dump.ToArray(TagDump.DataLength)[..16], read.Data);
        Assert.Contains("READ 00", session.Log.ToText());
    }

    [Fact]
    public async Task Session_CanPreloadReadsIntoTheFlipper()
    {
        var flipper = new FakeFlipper();
        var dump = TestData.Amiibo();
        var tag = Ntag215Simulator.FromAmiiboDump(dump);

        await using var session = await EmulationSession.StartAsync(flipper, tag, "Test amiibo", useAnswerTable: true);

        Assert.True(session.IsRunning);
        Assert.True(session.UsesAnswerTable);
        Assert.Equal("Fake Flipper", session.Hello!.Firmware);
        Assert.True(session.Hello.Capabilities.HasFlag(BridgeCapabilities.ListenA));

        // LISTEN tech A with the model's UID, ATQA and SAK.
        var listen = flipper.ListenParameters!;
        Assert.Equal((byte)BridgeTech.Iso14443A, listen[0]);
        Assert.Equal((byte)BridgeListenFlags.Default, listen[1]);
        Assert.Equal(7, listen[4]);
        Assert.Equal(tag.Uid7, listen[5..12]);
        Assert.Equal(tag.Atqa, listen[12..14]);

        // READs, GET_VERSION and READ_SIG answered by the Flipper.
        Assert.True(flipper.Table.Count > 100);
        Assert.Contains("3C00", flipper.Table.Keys);
        Assert.Equal(dump.ToArray(TagDump.DataLength)[..16], flipper.Table["3000"].Data);
        Assert.Equal(Ntag215Simulator.VersionNtag215, flipper.Table["60"].Data);
        Assert.True(flipper.Table["3000"].Flags.HasFlag(BridgeTxFlags.AddCrc));

        // Everything else relayed.
        var read = await flipper.ReaderSendsAsync(0x3A, 0x00, 0x86);
        Assert.Equal(135 * 4 * 8, read.Bits);
        Assert.Equal(dump.ToArray(TagDump.DataLength)[..(130 * 4)], read.Data[..(130 * 4)]);

        flipper.TableAnswered(0x30, 0x04);
        flipper.Send(BridgeMessageType.FieldOff, 0, [0, 0, 0, 0]);
        await session.RefreshStatusAsync();
        Assert.Equal(7u, session.DeviceStatus!.TableHits);
        Assert.Equal(1, session.TableHits);
        Assert.False(flipper.HasAnswerQueued);

        var log = session.Log.ToText();
        Assert.Contains("FAST_READ 00-86", log);
        Assert.Contains("540 bytes", log);
        Assert.Contains("answer table", log);
        Assert.Contains("Field off", log);
        Assert.False(session.FieldPresent);
    }

    [Fact]
    public async Task Session_UpdatesTheTableAfterAWrite()
    {
        var flipper = new FakeFlipper();
        var tag = Ntag215Simulator.FromAmiiboDump(TestData.Amiibo());
        await using var session = await EmulationSession.StartAsync(flipper, tag, "Test amiibo", useAnswerTable: true);

        var pack = await flipper.ReaderSendsAsync([0x1B, .. NtagUid.AmiiboPassword(TestData.Amiibo().Uid)]);
        Assert.Equal(16, pack.Bits);
        var ack = await flipper.ReaderSendsAsync(0xA2, 0x40, 0xDE, 0xAD, 0xBE, 0xEF);
        Assert.Equal(new byte[] { 0x0A }, ack.Data);

        // READ 3D..40 all cover page 40 now.
        await Eventually(() => flipper.Table["3040"].Data[..4].SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, flipper.Table["303D"].Data[12..16]);
    }

    [Fact]
    public async Task Session_NoticesTheFlipperGoingAway()
    {
        var flipper = new FakeFlipper();
        await using var session = await EmulationSession.StartAsync(flipper, Ntag215Simulator.CreateBlank(), "Blank");
        var closed = new TaskCompletionSource();
        session.Changed += () =>
        {
            if (!session.IsRunning)
            {
                closed.TrySetResult();
            }
        };

        flipper.Unplug();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(session.Error);
        Assert.Contains("lost", session.Log.ToText());
    }

    [Fact]
    public async Task Bridge_TransceivesInReaderMode()
    {
        var flipper = new FakeFlipper();
        await using var bridge = new FlipperBridge(flipper);
        bridge.Start();
        await bridge.HelloAsync();

        await bridge.ReaderAsync(BridgeTech.Iso15693);
        var result = await bridge.TransceiveAsync(new byte[] { 0x26, 0x01, 0x00 }, BridgeTxFlags.AddCrc | BridgeTxFlags.CheckCrc);

        Assert.True(result.Success);
        Assert.True(result.CrcOk);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x26 }, result.Data);
        Assert.Equal(TimeSpan.FromMicroseconds(300), result.Elapsed);
    }

    [Fact]
    public async Task Bridge_ReportsRefusedCommands()
    {
        var flipper = new FakeFlipper { NextStatus = BridgeStatusCode.Unsupported };
        await using var bridge = new FlipperBridge(flipper);
        bridge.Start();
        await bridge.HelloAsync();

        var error = await Assert.ThrowsAsync<BridgeCommandException>(() => bridge.ReaderAsync(BridgeTech.Iso14443B));
        Assert.Equal(BridgeStatusCode.Unsupported, error.Status);
    }

    [Fact]
    public void CommandNames_DescribeCommonCommands()
    {
        Assert.Equal("READ 10", NfcCommandNames.Describe([0x30, 0x10]));
        Assert.Equal("PWD_AUTH", NfcCommandNames.Describe([0x1B, 1, 2, 3, 4]));
        Assert.Equal("N2 FAST_READ 00-3E bank 5", NfcCommandNames.Describe([0x3B, 0x00, 0x3E, 0x05]));
    }
}
