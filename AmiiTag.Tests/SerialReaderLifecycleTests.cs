using System.Threading.Channels;
using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;

namespace AmiiTag.Tests;

public sealed class SerialReaderLifecycleTests
{
    [Fact]
    public Task Mfrc522_CancelledDrain_ReleasesPortBeforeProviderSwitch() => VerifyCleanupAsync("drain");

    [Fact]
    public Task Mfrc522_FailedVersionProbe_ReleasesPortBeforeProviderSwitch() => VerifyCleanupAsync("version");

    [Fact]
    public Task Mfrc522_FailedInitialization_ReleasesPortBeforeProviderSwitch() => VerifyCleanupAsync("init");

    private static async Task VerifyCleanupAsync(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var factory = new ExclusivePorts(stage, cancellation);
        await using var provider = new Mfrc522NfcProvider(factory);
        if (stage == "drain")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ConnectAsync(new NfcConnectOptions(), cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => provider.ConnectAsync(new NfcConnectOptions(), cancellation.Token));
        // These failures occur before _reader is assigned. ReleaseAsync alone could not find the leaked handle.
        // Another provider must be able to open the same port immediately, without waiting for finalization.
        Assert.True(factory.Last!.Closed);
        await using var nextProviderPort = await factory.OpenAsync("COM12", 115200);
    }

    private sealed class ExclusivePorts(string failure, CancellationTokenSource cancellation) : ISerialPortFactory
    {
        public bool IsSupported => true;
        public Port? Last;
        public Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NfcReaderInfo>>([new("COM12", "USB UART")]);
        public Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default)
        {
            if (Last is { Closed: false }) throw new IOException("COM12 is still in use.");
            Last = new Port(failure, cancellation);
            return Task.FromResult<ISerialPortStream>(Last);
        }
    }

    private sealed class Port(string failure, CancellationTokenSource cancellation) : ISerialPortStream
    {
        private readonly Channel<byte> _input = Channel.CreateUnbounded<byte>();
        public bool Closed;
        public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            if (data.SequenceEqual(new byte[] { 0xB7 }))
            {
                if (failure == "version") throw new IOException("Adapter disconnected during the version probe.");
                _input.Writer.TryWrite(0x92);
            }
            else if (failure == "init") throw new IOException("Adapter disconnected during initialization.");
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            if (failure == "drain")
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            buffer[offset] = await _input.Reader.ReadAsync(cancellationToken);
            return 1;
        }
        public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
    }
}
