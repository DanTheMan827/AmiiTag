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
    public async Task WaitingFigureUsesActivationUidAndCapturesSramBeforeAnyEepromRead()
    {
        var figure = AmiiboV3Dump.Create(Hex.FromHex(AmiiboV3Tests.V3Id), TestData.Keys,
            new byte[] { 4, 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3 });
        var tag = NtagI2cPlusSimulator.FromAmiiboImage(figure);
        await using var connection = new RecordingConnection(new SimulatedTagConnection(tag));

        var dump = await AmiiboV3NfcOperations.ReadV3Async(connection);

        Assert.True(dump.HasLockOn);
        var commands = connection.Commands.Select(c => c.Bytes).ToList();
        var capture = commands.FindIndex(c => c is [0x3A, 0xF0, _]);
        Assert.True(capture > 0);
        Assert.DoesNotContain(commands.Take(capture), c => c[0] == 0x30 ||
            (c[0] == 0x3A && c[1] < 0xEC));
        Assert.Contains(commands.Take(capture), c => c.SequenceEqual(new byte[]
            { 0x1B, 0xA8, 0x53, 0xAC, 0x57 }));
        Assert.Equal(0, tag.FailedAuthAttempts);
    }

    [Fact]
    public async Task WaitingLoopPollsOnlyEdAndDoesNotReadSramBeforeReady()
    {
        var figure = AmiiboV3Dump.Create(Hex.FromHex(AmiiboV3Tests.V3Id), TestData.Keys,
            new byte[] { 4, 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3 });
        var tag = NtagI2cPlusSimulator.FromAmiiboImage(figure);
        await using var connection = new RecordingConnection(new SimulatedTagConnection(tag))
            { RequireStatusOnlyPolling = true };

        var dump = await AmiiboV3NfcOperations.ReadV3Async(connection);

        Assert.True(dump.HasLockOn);
        Assert.True(connection.StatusPolls >= 2);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(15, false)]
    [InlineData(32, true)]
    [InlineData(15, true)]
    public async Task ReadyStatusIsFollowedImmediatelyBySram_AndRequestEchoKeepsPolling(int batch, bool echoFirst)
    {
        var expected = LockOnResponse.Normalize(new byte[] { 2, 3, 4 });
        await using var connection = new LockOnReplayConnection(batch, echoFirst, expected);
        byte[] session = [0x41, 0x00, 0xF8, 0x48, 0x08, 0x01, 0x21, 0x00];

        var response = await AmiiboV3NfcOperations.ReadLockOnAsync(connection, session, TimeSpan.FromSeconds(1));

        Assert.Equal(expected, response);
        Assert.Equal(echoFirst ? 2 : 1, connection.Polls);
        Assert.True(connection.Finished);
        Assert.Equal(0x21, session[6]); // The caller's snapshot is not mutated.
    }

    [Fact]
    public async Task RepeatedRequestEchoStopsAtTheOriginalDeadline()
    {
        await using var connection = new LockOnReplayConnection(32, true,
            AmiiboV3NfcOperations.LockOnRequest, repeatEcho: true);
        byte[] session = [0x41, 0x00, 0xF8, 0x48, 0x08, 0x01, 0x21, 0x00];

        var response = await AmiiboV3NfcOperations.ReadLockOnAsync(connection, session,
            TimeSpan.FromMilliseconds(40)).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(response);
        Assert.True(connection.Polls > 0);
    }

    // Script the status and SRAM exchanges from the failing log, with a genuine response on
    // the next poll after an echo. Any intervening EC-ED read is an unexpected command.
    private sealed class LockOnReplayConnection : INfcTagConnection
    {
        private readonly Queue<(byte[] Command, byte[] Response)> _steps = new();
        private readonly bool _repeatEcho;
        public LockOnReplayConnection(int batch, bool echoFirst, byte[] expected, bool repeatEcho = false)
        {
            _repeatEcho = repeatEcho;
            MaxFastReadPages = batch;
            if (echoFirst) AddCapture(AmiiboV3NfcOperations.LockOnRequest);
            AddCapture(expected);
        }
        private void AddCapture(byte[] payload)
        {
            _steps.Enqueue((new byte[] { 0x3A, 0xED, 0xED }, new byte[] { 0x08, 0x01, 0x29, 0x00 }));
            if (MaxFastReadPages >= 16)
                _steps.Enqueue((new byte[] { 0x3A, 0xF0, 0xFF }, payload));
            else
            {
                _steps.Enqueue((new byte[] { 0x3A, 0xF0, 0xF7 }, payload[..32]));
                _steps.Enqueue((new byte[] { 0x3A, 0xF8, 0xFF }, payload[32..]));
            }
        }
        public int Polls { get; private set; }
        public bool Finished => _steps.Count == 0;
        public byte[] Uid => [];
        public int MaxFastReadPages { get; }
        public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_repeatEcho && _steps.Count == 0) AddCapture(AmiiboV3NfcOperations.LockOnRequest);
            Assert.NotEmpty(_steps);
            var step = _steps.Dequeue();
            Assert.Equal(step.Command, command);
            if (command is [0x3A, 0xED, 0xED]) Polls++;
            return Task.FromResult(step.Response);
        }
        public Task SendAsync(byte[] command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public void SetStatus(string message) { }
        public Task CloseAsync(string? errorMessage = null) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CancellationDuringAuthenticationWaitDoesNotSendTheRequest()
    {
        var tag = NtagI2cPlusSimulator.FromAmiiboImage(AmiiboV3Dump.Create(Hex.FromHex(AmiiboV3Tests.V3Id),
            TestData.Keys, new byte[] { 4, 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3 }));
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
        public bool RequireStatusOnlyPolling { get; init; }
        public int StatusPolls { get; private set; }
        private bool _requestSent;
        private bool _responseReady;
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
            if (RequireStatusOnlyPolling && _requestSent && !_responseReady &&
                command is not [0x3A, 0xED, 0xED])
                throw new InvalidOperationException("Only the status page may be polled before readiness.");
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
            if (command[0] == 0xA6)
                _requestSent = true;
            if (command is [0x3A, 0xED, 0xED])
            {
                StatusPolls++;
                _responseReady = response.Length == 4 && (response[2] & 0x08) != 0;
            }
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
