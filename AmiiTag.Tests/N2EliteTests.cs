using AmiiTag.Nfc;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// The N2 Elite client against a simulated N2 built from the community's traces of the official app.
/// </summary>
public class N2EliteTests
{
    private static (N2EliteSimulator Tag, SimulatedTagConnection Connection, N2EliteClient Client) Create(int banks = 4, int maxFastReadPages = 32)
    {
        var tag = new N2EliteSimulator(banks);
        var connection = new SimulatedTagConnection(tag, maxFastReadPages);
        return (tag, connection, new N2EliteClient(connection));
    }

    private static byte[] WithSignature(TagDump dump, byte fill)
    {
        var signature = new byte[TagDump.SignatureLength];
        Array.Fill(signature, fill);
        return [.. dump.ToArray(TagDump.DataLength), .. signature];
    }

    [Fact]
    public void Simulator_LooksLikeAnNtag215()
    {
        var tag = new N2EliteSimulator();
        Assert.Equal(Ntag215Simulator.VersionNtag215, tag.Transceive([0x60]).Data);
        Assert.All(tag.Transceive([0x30, 0x00]).Data, b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public async Task Detect_ReadsInfoAndId()
    {
        var (tag, connection, _) = Create(banks: 16);
        tag.ActiveBank = 2;

        var info = await N2EliteClient.DetectAsync(connection);

        Assert.NotNull(info);
        Assert.Equal(2, info!.ActiveBank);
        Assert.Equal(16, info.BankCount);
        Assert.Equal(3, info.FirmwareVersion);
        Assert.False(info.ButtonPressed);
        Assert.Equal("214b8702523d0d10163a99", info.IdHex);
        Assert.False(info.IsSingleTagEmulator);
        Assert.Equal(16, info.UsableBanks);
        Assert.Equal(new byte[] { 0x55 }, tag.CommandLog[0]);
        Assert.Equal(new byte[] { 0x43 }, tag.CommandLog[1]);
    }

    [Fact]
    public async Task Detect_AcceptsTheTwoByteInfoOfOlderUnits()
    {
        var (tag, connection, _) = Create(banks: 5);
        tag.ShortInfo = true;

        var info = await N2EliteClient.DetectAsync(connection);

        Assert.NotNull(info);
        Assert.Equal(5, info!.BankCount);
        Assert.Null(info.FirmwareVersion);
    }

    [Fact]
    public async Task Detect_ReturnsNullForARealNtag215()
    {
        var tag = Ntag215Simulator.FromAmiiboDump(TestData.Amiibo());
        var connection = new SimulatedTagConnection(tag);

        Assert.Null(await N2EliteClient.DetectAsync(connection));
    }

    [Fact]
    public async Task Detect_ReturnsNullWhenLocked()
    {
        var (tag, connection, _) = Create();
        tag.IsLocked = true;

        Assert.Null(await N2EliteClient.DetectAsync(connection));
    }

    [Fact]
    public async Task Detect_RefusesReadersWithoutDirectCommands()
    {
        var tag = new N2EliteSimulator();
        var connection = new SimulatedTagConnection(tag) { IsDirectCommandMode = false };

        await Assert.ThrowsAsync<NfcException>(() => N2EliteClient.DetectAsync(connection));
    }

    [Fact]
    public async Task WriteBank_WritesEveryPageOfThatBankOnly()
    {
        var (tag, _, client) = Create();
        var dump = new TagDump(TestData.Amiibo().ToArray(TagDump.DataLength));

        await client.WriteBankAsync(2, dump);

        Assert.Equal(dump.ToArray(TagDump.DataLength), tag.GetBankImage(2));
        Assert.All(tag.GetBankImage(0), b => Assert.Equal(0xFF, b));
        Assert.All(tag.GetBankImage(1), b => Assert.Equal(0xFF, b));

        // Page by page with N2_WRITE, in order, on bank 2; no signature since the dump has none.
        var writes = tag.CommandLog.Where(c => c[0] == 0xA5).ToList();
        Assert.Equal(TagDump.PageCount, writes.Count);
        Assert.Equal(Enumerable.Range(0, TagDump.PageCount), writes.Select(w => (int)w[1]));
        Assert.All(writes, w => Assert.Equal(2, w[2]));
    }

    [Fact]
    public async Task WriteBank_StoresTheSignatureWhenTheDumpHasOne()
        => await WriteBank_StoresSignature(0x5A);

    private static async Task WriteBank_StoresSignature(byte fill)
    {
        var (tag, _, client) = Create();
        var dump = new TagDump(WithSignature(TestData.Amiibo(), fill));

        await client.WriteBankAsync(1, dump);

        Assert.All(tag.GetBankSignature(1), b => Assert.Equal(fill, b));
    }

    [Fact]
    public async Task WriteBank_StoresTheSignatureOfAFullDump()
    {
        var (tag, _, client) = Create();
        var dump = TestData.Amiibo();

        await client.WriteBankAsync(1, dump);

        Assert.Equal(dump.Signature, tag.GetBankSignature(1));
        Assert.Equal(TagDump.PageCount + N2EliteClient.SignaturePages, tag.CommandLog.Count(c => c[0] == 0xA5));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(200)]
    [InlineData(7)]
    public async Task ReadBank_RoundTripsWhateverTheReaderCanTakeInOneRead(int maxFastReadPages)
    {
        var (tag, _, client) = Create(maxFastReadPages: maxFastReadPages);
        var dump = TestData.Amiibo();
        tag.SetBankImage(3, dump.ToArray(TagDump.DataLength));

        var read = await client.ReadBankAsync(3);

        Assert.Equal(dump.ToArray(TagDump.DataLength), read.Data);
        Assert.False(read.HasSignature);

        // Never more than 63 pages per N2_FAST_READ, always for bank 3.
        var reads = tag.CommandLog.Where(c => c[0] == 0x3B).ToList();
        Assert.All(reads, r => Assert.True(r[2] - r[1] + 1 <= Math.Min(63, maxFastReadPages)));
        Assert.All(reads, r => Assert.Equal(3, r[3]));
    }

    [Fact]
    public async Task ReadBank_IncludesAStoredSignature()
    {
        var (_, _, client) = Create();
        var dump = new TagDump(WithSignature(TestData.Amiibo(), 0x33));
        await client.WriteBankAsync(0, dump);

        var read = await client.ReadBankAsync(0);

        Assert.Equal(TagDump.FullLength, read.Length);
        Assert.Equal(dump.Data, read.Data);
    }

    [Fact]
    public async Task Summaries_ListEveryEnabledBankAndSpotEmptyOnes()
    {
        var (tag, _, client) = Create(banks: 3);
        var dump = TestData.Amiibo();
        tag.SetBankImage(1, dump.ToArray(TagDump.DataLength));

        var banks = await client.ReadBankSummariesAsync(3);

        Assert.Equal(3, banks.Count);
        Assert.True(banks[0].IsEmpty);
        Assert.False(banks[1].IsEmpty);
        Assert.True(banks[2].IsEmpty);
        Assert.Equal(dump.AmiiboIdHex, banks[1].Summary.AmiiboIdHex);
        Assert.Equal(dump.ToArray(TagDump.DataLength).AsSpan(0, N2EliteClient.SummaryPages * 4).ToArray(),
            banks[1].Summary.Span[..(N2EliteClient.SummaryPages * 4)].ToArray());
    }

    [Fact]
    public async Task WipeBank_ReturnsItToFf()
    {
        var (tag, _, client) = Create();
        await client.WriteBankAsync(0, new TagDump(WithSignature(TestData.Amiibo(), 0x11)));

        await client.WipeBankAsync(0);

        Assert.All(tag.GetBankImage(0), b => Assert.Equal(0xFF, b));
        Assert.All(tag.GetBankSignature(0), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public async Task SelectBank_ChangesWhatTheConsoleReads()
    {
        var (tag, _, client) = Create();
        var dump = TestData.Amiibo();
        await client.WriteBankAsync(2, dump);

        await client.SelectBankAsync(2);

        Assert.Equal(2, tag.ActiveBank);
        var page15 = tag.Transceive([0x30, 0x15]).Data;
        Assert.Equal(dump.AmiiboId, page15[..8]);
    }

    [Fact]
    public async Task SetBankCount_ValidatesAndApplies()
    {
        var (tag, _, client) = Create(banks: 1);

        await client.SetBankCountAsync(200);
        Assert.Equal(200, tag.BankCount);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetBankCountAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetBankCountAsync(201));
    }

    [Fact]
    public async Task Lock_HidesTheVendorCommandsUntilUnlockedWithTheButton()
    {
        var (tag, connection, client) = Create();

        await client.LockAsync();
        Assert.True(tag.IsLocked);
        Assert.Null(await N2EliteClient.DetectAsync(connection));

        // The user holds the button while the unlock commands go out.
        tag.Activate();
        await client.UnlockAsync(_ =>
        {
            tag.ButtonPressed = true;
            return Task.CompletedTask;
        });

        Assert.False(tag.IsLocked);
        tag.ButtonPressed = false;
        Assert.NotNull(await N2EliteClient.DetectAsync(connection));
    }

    [Fact]
    public async Task Unlock_FailsWithoutTheButton()
    {
        var (tag, _, client) = Create();
        tag.IsLocked = true;

        await Assert.ThrowsAnyAsync<Exception>(() => client.UnlockAsync(_ => Task.CompletedTask));
        Assert.True(tag.IsLocked);
    }

    [Fact]
    public async Task Detect_RecognisesAPixlPlayingAnN2AsOneTag()
    {
        // What a Pixl.js's tag emulator answers (fw/application/src/ntag/ntag_emu_v2.c).
        var (tag, connection, _) = Create(banks: 0x32);
        tag.Id = N2EliteInfo.PixlEmulatorId;

        var info = await N2EliteClient.DetectAsync(connection);

        Assert.NotNull(info);
        Assert.True(info!.IsSingleTagEmulator);
        Assert.Equal(1, info.UsableBanks);
        Assert.Equal(0x32, info.BankCount);
    }

    [Fact]
    public async Task Bank_OutOfRangeIsRejectedBeforeSending()
    {
        var (tag, _, client) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReadBankAsync(200));
        Assert.Empty(tag.CommandLog);
    }
}
