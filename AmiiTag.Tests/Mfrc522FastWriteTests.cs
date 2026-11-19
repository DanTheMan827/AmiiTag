using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

public class Mfrc522FastWriteTests
{
    [Theory]
    [InlineData(115200, 0)]
    [InlineData(230400, 0)]
    [InlineData(460800, 0)]
    [InlineData(921600, 0)]
    [InlineData(115200, 128)]
    [InlineData(921600, 128)]
    public async Task NativeFastWriteRefillsFifoInOneUartBatch(int baud, int startupMicroseconds)
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        tag.SramMirrored = true;
        var chip = new SimulatedMfrc522Bridge(tag);
        var provider = new Mfrc522NfcProvider(new SimulatedSerialPortFactory(chip)) { BaudRate = baud };
        await using var connection = await provider.ConnectAsync(new NfcConnectOptions());
        chip.SimulateTransmitTiming = true;
        // Arbitrarily slow PC/USB round trips are allowed OUTSIDE the continuous burst.
        chip.InterBatchDelay = TimeSpan.FromMilliseconds(20);
        chip.TransmitStartupDelay = TimeSpan.FromMicroseconds(startupMicroseconds);
        byte[] payload = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        byte[] command = [0xA6, 0xF0, 0xFF, .. payload];
        var framesBefore = chip.AirFrames.Count;

        Assert.Equal(new byte[] { 0x0A }, await connection.TransceiveAsync(command));

        Assert.Equal(payload, tag.LastSramRequest);
        Assert.Equal(Iso14443Crc.Append(command), Assert.Single(chip.AirFrames.Skip(framesBefore)));
        Assert.InRange(chip.PeakTransmitFifo, 38, 64);
        Assert.Equal(0, chip.GetRegister(Mfrc522Reg.Error) & Mfrc522Reg.ErrBufferOverflow);
        var burst = Assert.Single(chip.SerialWriteBatches, b =>
            b.Length >= 2 && b[0] == Mfrc522Reg.BitFraming && b[1] == Mfrc522Reg.StartSend);
        Assert.Equal(64, burst.Length);
        var tail = Iso14443Crc.Append(command)[38..];
        for (var i = 0; i < tail.Length; i++)
        {
            Assert.Equal(Mfrc522Reg.FifoData, burst[2 + i * 2]);
            Assert.Equal(tail[i], burst[3 + i * 2]);
        }
        Assert.Equal(15, connection.MaxFastReadPages);
        // Every UART write echo was consumed; the next operation is still synchronized.
        Assert.Equal(16, (await connection.TransceiveAsync(new byte[] { 0x30, 0x00 })).Length);
    }

    [Theory]
    [InlineData(9600)]
    [InlineData(57600)]
    [InlineData(1228800)]
    public async Task UnsupportedStreamingRateIsRejectedBeforeAnyRegisterOrRfAccess(int baud)
    {
        var chip = new SimulatedMfrc522Bridge(NtagI2cPlusSimulator.CreateBlank());
        var provider = new Mfrc522NfcProvider(new SimulatedSerialPortFactory(chip)) { BaudRate = baud };
        await using var connection = await provider.ConnectAsync(new NfcConnectOptions());
        var reads = chip.RegisterReads;
        var writes = chip.RegisterWrites;
        var frames = chip.AirFrames.Count;
        byte[] command = [0xA6, 0xF0, 0xFF, .. new byte[64]];

        await Assert.ThrowsAsync<NfcCommandNotSupportedException>(() => connection.TransceiveAsync(command));

        Assert.Equal(reads, chip.RegisterReads);
        Assert.Equal(writes, chip.RegisterWrites);
        Assert.Equal(frames, chip.AirFrames.Count);
        Assert.Equal(16, (await connection.TransceiveAsync(new byte[] { 0x30, 0x00 })).Length);
    }

    [Fact]
    public async Task SendAsyncAlsoSupportsNativeFastWrite()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        tag.SramMirrored = true;
        var chip = new SimulatedMfrc522Bridge(tag);
        var provider = new Mfrc522NfcProvider(new SimulatedSerialPortFactory(chip));
        await using var connection = await provider.ConnectAsync(new NfcConnectOptions());
        chip.SimulateTransmitTiming = true;
        var reader = new Mfrc522Reader(new Mfrc522Uart(chip, 921600));
        byte[] command = [0xA6, 0xF0, 0xFF, .. new byte[64]];

        await reader.SendAsync(command);

        Assert.Equal(command[3..], tag.LastSramRequest);
        Assert.Equal(Iso14443Crc.Append(command), chip.AirFrames.Last());
    }

    [Fact]
    public async Task V3ReadUsesNativeFastWriteInsteadOfPageWrites()
    {
        var figure = AmiiboV3Dump.Create(Hex.FromHex(AmiiboV3Tests.V3Id), TestData.Keys,
            new byte[] { 4, 1, 2, 3, 4, 5, 6 }, new byte[] { 2, 3, 4 });
        var tag = NtagI2cPlusSimulator.FromAmiiboImage(figure);
        var chip = new SimulatedMfrc522Bridge(tag);
        var provider = new Mfrc522NfcProvider(new SimulatedSerialPortFactory(chip));
        await using var connection = await provider.ConnectAsync(new NfcConnectOptions());
        chip.SimulateTransmitTiming = true;

        var dump = await AmiiboV3NfcOperations.ReadV3Async(connection);

        Assert.True(dump.HasLockOn);
        Assert.Equal(figure.LockOn, dump.LockOn);
        Assert.Single(chip.AirFrames, f => f[0] == 0xA6 && f.Length == 69 && Iso14443Crc.Check(f));
        Assert.DoesNotContain(chip.AirFrames, f => f[0] == 0xA2);
    }
}
