using AmiiTag.Nfc;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

public class V3DetectionRecoveryTests
{
    [Fact]
    public async Task ReadableV3DoesNotDependOnGetVersion()
    {
        var figure = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys);
        var tag = NtagI2cPlusSimulator.FromAmiiboImage(figure);
        await using var connection = new SimulatedTagConnection(tag, unsupportedCommands: [0x60]);
        Assert.IsType<AmiiboV3Dump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection));
        Assert.DoesNotContain(tag.CommandLog, command => command[0] == 0x60);
    }

    [Fact]
    public async Task AFailedReselectDoesNotRunTheReadBeforeRestorationSucceeds()
    {
        var figure = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys).WithLockOn(new byte[] { 1, 2, 3 });
        await using var connection = new RecoveryConnection(new SimulatedTagConnection(NtagI2cPlusSimulator.FromAmiiboImage(figure)));
        var dump = await AmiiboV3NfcOperations.ReadV3Async(connection);
        Assert.True(dump.HasAmiiboId);
        Assert.Equal(3, connection.Reselects);
        Assert.Equal(3, connection.BulkAttempts); // Failed sector-0 chunk, restored retry, sector-1 first chunk.
    }

    private sealed class RecoveryConnection(SimulatedTagConnection inner) : INfcTagConnection, INfcReselectable
    {
        private bool _needsRestore;
        public int Reselects
        {
            get; private set;
        }
        public int BulkAttempts
        {
            get; private set;
        }
        public byte[] Uid => inner.Uid;
        public int MaxFastReadPages => inner.MaxFastReadPages;
        public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
        {
            if (_needsRestore)
                throw new InvalidOperationException("A read ran before tag restoration succeeded.");
            if (command is [0x3A, 0, > 1] && ++BulkAttempts == 1)
            {
                _needsRestore = true;
                throw new NfcException("Transient tag refusal");
            }
            return inner.TransceiveAsync(command, cancellationToken);
        }
        public Task ReselectAsync(CancellationToken cancellationToken = default)
        {
            if (++Reselects < 3)
                throw new NfcException("Not reselected yet");
            _needsRestore = false;
            return Task.CompletedTask;
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
