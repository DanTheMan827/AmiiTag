using System.Reflection;
using AmiiTag.Nfc;
using AmiiTag.Nfc.PcSc;
using AmiiTag.Tags;
using PCSC;
using PCSC.Exceptions;

namespace AmiiTag.Tests;

public class PcscRecoveryTests
{
    [Theory]
    [InlineData("ACS ACR122 0", false)]
    [InlineData("ACS ACR1552U PICC 0", true)]
    public void SelectorRetriesTheSameProtocolAfterRecoveringAStaleHandle(string name, bool transparent)
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        var beginAttempts = 0;
        var read = new byte[16];
        script.Send = command =>
        {
            var begin = transparent ? command.SequenceEqual(new byte[] { 0xFF, 0xC2, 0, 0, 2, 0x81, 0 })
                : command.SequenceEqual(new byte[] { 0xFF, 0, 0x48, 0, 0 });
            if (begin && ++beginAttempts == 1) throw new PCSCException(SCardError.RemovedCard);
            if (begin && !transparent) return "ACR122U"u8.ToArray();
            if (transparent)
            {
                if (command[3] == 1) return [0x97, 16, .. read, 0x90, 0];
                return [0x90, 0];
            }
            return command[6] switch
            {
                0x08 => [0xD5, 0x09, 0x90, 0],
                0x32 => [0xD5, 0x33, 0x90, 0],
                0x42 => [0xD5, 0x43, 0, .. Iso14443Crc.Append(read), 0x90, 0],
                _ => throw new InvalidOperationException(Hex.ToSpacedHex(command))
            };
        };
        using var card = new PcscCard(reader);

        var strategy = new PcscStrategySelector().Select(name, card);

        Assert.Equal(transparent ? "PC/SC transparent session" : "PN53x direct transmit", strategy.Name);
        Assert.Equal(2, beginAttempts);
        Assert.Equal(new[] { SCardReaderDisposition.Leave }, script.Reconnects);
        if (!transparent)
            Assert.DoesNotContain(script.Commands, c => c.Length > 6 && c[6] == 0x08 && c[^1] == 0x80);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassiveAckDisconnectRebindsWithoutResetOrReplayingThePacket(bool transparent)
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        script.Send = c => c is [0xFF, 0xCA, 0, 0, 0]
            ? new byte[] { 4, 1, 2, 3, 4, 5, 6, 0x90, 0 }
            : throw new PCSCException(SCardError.RemovedCard);
        using var card = new PcscCard(reader);
        var strategy = new DisconnectingStrategy(transparent);
        var context = DispatchProxy.Create<ISCardContext, ScriptedContext>();
        var type = typeof(PcscNfcProvider).Assembly.GetType("AmiiTag.Nfc.PcSc.PcscTagConnection")!;
        await using var connection = (INfcTagConnection)Activator.CreateInstance(type, context, card, strategy, new byte[] { 4, 1, 2, 3, 4, 5, 6 })!;

        await AmiiboV3NfcOperations.SectorSelectAsync(connection, 1);

        Assert.Equal(1, strategy.SecondPackets);
        Assert.Equal(TimeSpan.FromMilliseconds(10), strategy.Timeout);
        Assert.Equal(new[] { SCardReaderDisposition.Leave }, script.Reconnects);
    }

    [Fact]
    public void Pn532ShortTimeoutIsRoundedUpAndRestoredAfterPassiveAck()
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        script.Send = c => c[6] switch
        {
            0x32 => [0xD5, 0x33, 0x90, 0],
            0x08 => [0xD5, 0x09, 0x90, 0],
            _ => [0xD5, 0x43, 1, 0x90, 0]
        };
        using var card = new PcscCard(reader);
        var strategy = new Pn53xDirectTransmitStrategy();
        Assert.Throws<NfcException>(() => strategy.Transceive(card, [1, 0, 0, 0], TimeSpan.FromMilliseconds(10)));
        var configurations = script.Commands.Where(c => c[6] == 0x32).ToList();
        Assert.Equal(2, configurations.Count);
        Assert.Equal(0x08, configurations[0][^1]); // 12.8 ms
        Assert.Equal(0x0E, configurations[1][^1]); // 819.2 ms
    }

    [Fact]
    public async Task FailedLeaveRecoveryNeverFallsBackToReset()
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        script.FailReconnect = true;
        using var card = new PcscCard(reader);
        var context = DispatchProxy.Create<ISCardContext, ScriptedContext>();
        var type = typeof(PcscNfcProvider).Assembly.GetType("AmiiTag.Nfc.PcSc.PcscTagConnection")!;
        await using var connection = (INfcTagConnection)Activator.CreateInstance(type, context, card,
            new DisconnectingStrategy(false), new byte[] { 4, 1, 2, 3, 4, 5, 6 })!;
        await Assert.ThrowsAsync<NfcConnectionLostException>(() => AmiiboV3NfcOperations.SectorSelectAsync(connection, 1));
        Assert.Equal(3, script.Reconnects.Count);
        Assert.All(script.Reconnects, disposition => Assert.Equal(SCardReaderDisposition.Leave, disposition));
    }

    [Fact]
    public async Task FailureBeforeTheSectorPacketIsSentIsNotAPassiveAck()
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        script.Send = _ => throw new PCSCException(SCardError.RemovedCard);
        using var card = new PcscCard(reader);
        var strategy = new Pn53xDirectTransmitStrategy();
        // RFConfiguration failed; treating this as silence would claim a sector was selected without sending it.
        await Assert.ThrowsAsync<NfcConnectionLostException>(async () =>
        {
            await Task.Yield();
            strategy.Transceive(card, [1, 0, 0, 0], TimeSpan.FromMilliseconds(10));
        });
        Assert.Single(script.Commands);
        Assert.Equal(0x32, script.Commands[0][6]);
        Assert.Empty(script.Reconnects);
    }

    [Fact]
    public void TransparentSectorPacketUsesAnExplicitTenMillisecondTimer()
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        script.Send = _ => [0x90, 0];
        using var card = new PcscCard(reader);
        new TransparentSessionStrategy().Transceive(card, [1, 0, 0, 0], TimeSpan.FromMilliseconds(10));
        Assert.Equal(new byte[] { 0x5F, 0x46, 4, 0x10, 0x27, 0, 0 }, script.Commands.Single()[5..12]);
    }

    [Fact]
    public void AStartupReadTimeoutReactivatesTheSameTagWithoutCardReset()
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        byte[] uid = [4, 1, 2, 3, 4, 5, 6];
        var readAttempts = 0;
        script.Send = c =>
        {
            if (c is [0xFF, 0, 0x48, 0, 0]) return "ACR122U"u8.ToArray();
            if (c is [0xFF, 0xCA, 0, 0, 0]) return [.. uid, 0x90, 0];
            return c[6] switch
            {
                0x08 => [0xD5, 0x09, 0x90, 0],
                0x32 => [0xD5, 0x33, 0x90, 0],
                0x4A => [0xD5, 0x4B, 1, 1, 0x44, 0, 0, 7, .. uid, 0x90, 0],
                0x42 when ++readAttempts == 1 => [0xD5, 0x43, 1, 0x90, 0],
                0x42 => [0xD5, 0x43, 0, .. Iso14443Crc.Append(new byte[16]), 0x90, 0],
                _ => throw new InvalidOperationException(Hex.ToSpacedHex(c))
            };
        };
        using var card = new PcscCard(reader);
        Assert.IsType<Pn53xDirectTransmitStrategy>(new PcscStrategySelector().Select("ACS ACR122 0", card));
        Assert.Equal(2, readAttempts);
        Assert.Empty(script.Reconnects);
        Assert.Single(script.Commands, c => c.Length > 6 && c[6] == 0x4A);
        Assert.DoesNotContain(script.Commands, c => c.Length > 7 && c[6] == 0x32 && c[7] == 0x01);
    }

    [Fact]
    public void Pn532ActivationRejectsADifferentUid()
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        script.Send = c => c[6] switch
        {
            0x08 => [0xD5, 0x09, 0x90, 0],
            0x32 => [0xD5, 0x33, 0x90, 0],
            0x4A => [0xD5, 0x4B, 1, 1, 0x44, 0, 0, 7, 4, 9, 9, 9, 9, 9, 9, 0x90, 0],
            _ => throw new InvalidOperationException()
        };
        using var card = new PcscCard(reader);
        Assert.Throws<NfcConnectionLostException>(() => new Pn53xDirectTransmitStrategy().Reselect(card, [4, 1, 2, 3, 4, 5, 6]));
    }

    [Fact]
    public void ReaderHelperRestoringHardwareCrcDoesNotBreakTheNextRawCommand()
    {
        var reader = DispatchProxy.Create<ICardReader, ScriptedReader>();
        var script = (ScriptedReader)reader;
        var hardwareCrc = true;
        script.Send = c =>
        {
            if (c is [0xFF, 0, 0x48, 0, 0]) return "ACR122U"u8.ToArray();
            if (c is [0xFF, 0xCA, 0, 0, 0])
            {
                hardwareCrc = true; // Emulate reader-managed framing changes during its own helper.
                return [4, 1, 2, 3, 4, 5, 6, 0x90, 0];
            }
            if (c[6] == 0x08)
            {
                hardwareCrc = c[^1] != 0;
                return [0xD5, 0x09, 0x90, 0];
            }
            if (c[6] == 0x32) return [0xD5, 0x33, 0x90, 0];
            if (hardwareCrc) return [0xD5, 0x43, 1, 0x90, 0];
            return [0xD5, 0x43, 0, .. Iso14443Crc.Append(new byte[16]), 0x90, 0];
        };
        using var card = new PcscCard(reader);
        var strategy = new Pn53xDirectTransmitStrategy();
        Assert.True(strategy.TryBegin(card));
        Assert.Equal(16, strategy.Transceive(card, [0x30, 0]).Length);
        Assert.Equal(7, card.GetUid().Length);
        Assert.Equal(16, strategy.Transceive(card, [0x30, 0x15]).Length);
    }

    private sealed class DisconnectingStrategy(bool transparent) : IPcscTransceiveStrategy
    {
        public string Name => transparent ? "transparent" : "PN532";
        public bool IsDirect => true;
        public int MaxFastReadPages => 16;
        public int SecondPackets { get; private set; }
        public TimeSpan? Timeout { get; private set; }
        public bool TryBegin(PcscCard card) => true;
        public byte[] Transceive(PcscCard card, byte[] command) => [0x0A];
        public byte[] Transceive(PcscCard card, byte[] command, TimeSpan responseTimeout)
        {
            SecondPackets++;
            Timeout = responseTimeout;
            throw new PCSCException(SCardError.RemovedCard);
        }
        public void End(PcscCard card) { }
    }

    public class ScriptedReader : DispatchProxy
    {
        public Func<byte[], byte[]> Send { get; set; } = _ => [];
        public bool FailReconnect { get; set; }
        public List<byte[]> Commands { get; } = [];
        public List<SCardReaderDisposition> Reconnects { get; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Protocol": return SCardProtocol.T1;
                case "Transmit":
                    var command = ((byte[])args![1]!).ToArray();
                    Commands.Add(command);
                    var response = Send(command);
                    response.CopyTo((byte[])args[4]!, 0);
                    return response.Length;
                case "Reconnect":
                    Reconnects.Add((SCardReaderDisposition)args![2]!);
                    if (FailReconnect) throw new PCSCException(SCardError.RemovedCard);
                    return null;
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    public class ScriptedContext : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }
}
