using AmiiTag.Nfc;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;
using System.Diagnostics;

namespace AmiiTag.Tests;

public class V3ReadSequenceTests
{
    [Theory]
    [InlineData(32)] // Phone NFC / PN532
    [InlineData(16)] // PC/SC transparent exchange
    [InlineData(15)] // MFRC522: FAST_WRITE exceeds the FIFO
    public async Task CapturesSramBeforeEeprom_ThenAuthenticatesAfterTheOnlySectorSwitch(int batch)
    {
        var raw = AmiiboV3Dump.Create(Hex.FromHex(AmiiboV3Tests.V3Id), TestData.Keys,
            new byte[] { 4, 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3 }).Data;
        raw[0xE7 * 4] |= AmiiboV3NfcOperations.PtI2c2kProt;
        var tag = NtagI2cPlusSimulator.FromAmiiboImage(new AmiiboV3Dump(raw));
        tag.SramReadyAfterSessionReads = 4;
        await using var connection = new RecordingConnection(new SimulatedTagConnection(tag, batch), batch < 16);

        var dump = await AmiiboV3NfcOperations.ReadV3Async(connection);

        Assert.True(dump.HasLockOn);
        Assert.Equal(raw.AsSpan(0x400).ToArray(), dump.Data.AsSpan(0x400).ToArray());
        var commands = connection.Commands;
        var firstAuth = commands.FindIndex(c => c.Bytes[0] == 0x1B);
        var request = commands.FindIndex(c => c.Bytes[0] == (batch < 16 ? 0xA2 : 0xA6));
        var capture = commands.FindIndex(c => c.Bytes is [0x3A, 0xF0, _]);
        var eeprom = commands.FindIndex(c => c.Bytes is [0x3A, 0x00, > 1]);
        var select = commands.FindIndex(c => c.Bytes is [0xC2, 0xFF]);
        Assert.True(firstAuth < request && request < capture && capture < eeprom && eeprom < select);
        Assert.Single(commands, c => c.Bytes is [0xC2, 0xFF]);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, commands[select + 1].Bytes);
        Assert.Equal(TimeSpan.FromMilliseconds(10), connection.SelectionTimeout);
        Assert.Equal(0x1B, commands[select + 2].Bytes[0]);
        Assert.Equal(1, tag.CurrentSector);
        Assert.Equal(2, commands.Count(c => c.Bytes[0] == 0x1B));
        foreach (var index in Enumerable.Range(0, commands.Count - 1).Where(i => commands[i].Bytes[0] == 0x1B))
        {
            Assert.True(Stopwatch.GetElapsedTime(commands[index].Timestamp, commands[index + 1].Timestamp)
                >= TimeSpan.FromMilliseconds(90), "The APK compatibility delay must occur before the next NFC command (10 ms timer tolerance).");
        }
    }

    [Fact]
    public async Task CancellationDuringAuthenticationWaitDoesNotSendTheRequest()
    {
        var tag = NtagI2cPlusSimulator.FromAmiiboImage(AmiiboV3Dump.Create(Hex.FromHex(AmiiboV3Tests.V3Id),
            TestData.Keys, new byte[] { 4, 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3 }));
        tag.SramReadyAfterSessionReads = 4;
        using var cts = new CancellationTokenSource();
        await using var connection = new RecordingConnection(new SimulatedTagConnection(tag)) { AfterAuth = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AmiiboV3NfcOperations.ReadV3Async(connection, cancellationToken: cts.Token));
        Assert.DoesNotContain(connection.Commands, c => c.Bytes[0] is 0xA6 or 0xA2 or 0xC2);
    }

    [Fact]
    public async Task LostTransportIsNotAcceptedAsASuccessfulSectorSwitch()
    {
        await using var connection = new RecordingConnection(null) { LoseTransport = true };
        await Assert.ThrowsAsync<NfcConnectionLostException>(() => AmiiboV3NfcOperations.SectorSelectAsync(connection, 1));
    }

    [Fact]
    public async Task ExplicitNakIsNotAcceptedAsAPassiveAck()
    {
        await using var connection = new RecordingConnection(null) { RejectSector = true };
        await Assert.ThrowsAsync<NfcNakException>(() => AmiiboV3NfcOperations.SectorSelectAsync(connection, 1));
    }

    private sealed class RecordingConnection(SimulatedTagConnection? inner, bool noFastWrite = false) : INfcTagConnection
    {
        private readonly byte[] _sramRequest = new byte[64];
        public List<(byte[] Bytes, long Timestamp)> Commands { get; } = [];
        public TimeSpan? SelectionTimeout
        {
            get; private set;
        }
        public Action? AfterAuth
        {
            get; init;
        }
        public bool LoseTransport
        {
            get; init;
        }
        public bool RejectSector
        {
            get; init;
        }
        public byte[] Uid => inner?.Uid ?? [];
        public int MaxFastReadPages => inner?.MaxFastReadPages ?? 32;
        public async Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.ToArray(), Stopwatch.GetTimestamp()));
            if (noFastWrite && command[0] == 0xA6)
                throw new NfcCommandNotSupportedException(0xA6);
            // The unchanged datasheet simulator models FAST_WRITE only. Adapt page writes in the test
            // transport so the reader's FIFO fallback can be exercised without changing that simulator.
            if (noFastWrite && command is [0xA2, >= 0xF0, _, _, _, _])
            {
                command.AsSpan(2).CopyTo(_sramRequest.AsSpan((command[1] - 0xF0) * 4));
                return command[1] == 0xFF
                    ? await inner!.TransceiveAsync([0xA6, 0xF0, 0xFF, .. _sramRequest], cancellationToken)
                    : new byte[] { 0x0A };
            }
            if (command is [<= 1, 0, 0, 0])
            {
                if (LoseTransport)
                    throw new NfcConnectionLostException("Lost handle", new IOException());
                if (RejectSector)
                    throw new NfcNakException(0, "Rejected");
            }
            var response = inner != null ? await inner.TransceiveAsync(command, cancellationToken) : new byte[] { 0x0A };
            if (command[0] == 0x1B)
                AfterAuth?.Invoke();
            return response;
        }
        public Task<byte[]> TransceiveAsync(byte[] command, TimeSpan responseTimeout, CancellationToken cancellationToken = default)
        {
            SelectionTimeout = responseTimeout;
            return TransceiveAsync(command, cancellationToken);
        }
        public void SetStatus(string message)
        {
        }
        public Task CloseAsync(string? errorMessage = null) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task SendAsync(byte[] command, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
