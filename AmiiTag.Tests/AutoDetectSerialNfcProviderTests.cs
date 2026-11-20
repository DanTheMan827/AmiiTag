using AmiiTag.Nfc;

namespace AmiiTag.Tests;

public sealed class AutoDetectSerialNfcProviderTests
{
    [Fact]
    public async Task Pn532AnsweringTheProbe_IsUsedWithoutTheMfrc522_AndRemembered()
    {
        var mfrc522 = new FakeProvider("mfrc522");
        var pn532 = new FakeProvider("pn532") { AnswersProbe = true };
        var auto = new AutoDetectSerialNfcProvider(mfrc522, pn532);

        await auto.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(1, pn532.Probes);
        Assert.Equal(1, pn532.Connects);
        Assert.Equal(0, mfrc522.Connects);

        // The probed port, without a second port picker.
        Assert.Equal("COM7", pn532.LastOptions!.ReaderId);
        Assert.False(pn532.LastOptions.AllowUserPrompts);

        // Next time straight to the PN532, without probing.
        await auto.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(1, pn532.Probes);
        Assert.Equal(2, pn532.Connects);
        Assert.Same(pn532, auto.DetectedOn("COM7"));
    }

    [Fact]
    public async Task NoProbeAnswer_AssumesAnMfrc522()
    {
        var mfrc522 = new FakeProvider("mfrc522");
        var pn532 = new FakeProvider("pn532");
        var auto = new AutoDetectSerialNfcProvider(mfrc522, pn532);

        await auto.ConnectAsync(new NfcConnectOptions { ReaderId = "COM7" });
        Assert.Equal(1, pn532.Probes);
        Assert.Equal(0, pn532.Connects);
        Assert.Equal(1, mfrc522.Connects);
        Assert.Same(mfrc522, auto.DetectedOn("COM7"));
    }

    [Fact]
    public async Task AFailingProbe_AssumesAnMfrc522()
    {
        var mfrc522 = new FakeProvider("mfrc522");
        var pn532 = new FakeProvider("pn532") { ProbeThrows = true };
        var auto = new AutoDetectSerialNfcProvider(mfrc522, pn532);

        await auto.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(1, mfrc522.Connects);
        Assert.Same(mfrc522, auto.DetectedOn("COM7"));
    }

    [Fact]
    public async Task NeitherAnswering_SaysBothWereTried()
    {
        var mfrc522 = new FakeProvider("mfrc522") { NotFound = true };
        var pn532 = new FakeProvider("pn532");
        var auto = new AutoDetectSerialNfcProvider(mfrc522, pn532);

        var error = await Assert.ThrowsAsync<NfcException>(() => auto.ConnectAsync(new NfcConnectOptions()));
        Assert.Contains("MFRC522", error.Message);
        Assert.Contains("PN532", error.Message);
        Assert.Equal(1, mfrc522.Releases);
        Assert.Null(auto.DetectedOn("COM7"));
    }

    [Fact]
    public async Task AReaderSwappedOnThePort_IsDetectedAgain()
    {
        var mfrc522 = new FakeProvider("mfrc522");
        var pn532 = new FakeProvider("pn532") { AnswersProbe = true };
        var auto = new AutoDetectSerialNfcProvider(mfrc522, pn532);
        await auto.ConnectAsync(new NfcConnectOptions());

        // The PN532 is gone and an MFRC522 is plugged in instead.
        pn532.NotFound = true;
        pn532.AnswersProbe = false;
        await auto.ConnectAsync(new NfcConnectOptions());
        Assert.Same(mfrc522, auto.DetectedOn("COM7"));
    }

    private sealed class FakeProvider(string id) : INfcProvider, INfcReleasable, INfcSerialProbe
    {
        public bool NotFound;
        public bool AnswersProbe;
        public bool ProbeThrows;
        public int Probes;
        public int Connects;
        public int Releases;
        public NfcConnectOptions? LastOptions;

        public string Id => id;
        public string DisplayName => id;
        public bool IsSupported => true;
        public bool ShowsSystemPrompt => false;
        public bool SupportsReaderSelection => true;

        public Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NfcReaderInfo>>([new NfcReaderInfo("COM7", "COM7")]);

        public Task<bool> ProbeAsync(string portId, CancellationToken cancellationToken = default)
        {
            Probes++;
            if (ProbeThrows)
            {
                throw new IOException("The port is in use.");
            }

            return Task.FromResult(AnswersProbe);
        }

        public Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default)
        {
            Connects++;
            LastOptions = options;
            if (NotFound)
            {
                throw new NfcReaderNotFoundException($"No {id} answered.", options.ReaderId ?? "COM7");
            }

            return Task.FromResult<INfcTagConnection>(null!);
        }

        public Task ReleaseAsync()
        {
            Releases++;
            return Task.CompletedTask;
        }
    }
}
