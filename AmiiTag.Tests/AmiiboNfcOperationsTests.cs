using AmiiTag.Nfc;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// The app's NFC read/write flows against a simulated NTAG215.
/// </summary>
public class AmiiboNfcOperationsTests
{
    [Fact]
    public async Task ReadTag_ReturnsTheWholeTagAndSignature()
    {
        var source = TestData.Amiibo();
        var tag = Ntag215Simulator.FromAmiiboDump(source);

        var dump = await AmiiboNfcOperations.ReadTagAsync(new SimulatedTagConnection(tag));

        Assert.Equal(TagDump.FullLength, dump.Length);
        Assert.Equal(source.Span[..532].ToArray(), dump.Span[..532].ToArray());
        Assert.Equal(new byte[8], dump.Span[532..540].ToArray()); // PWD and PACK read as zeros.
        Assert.Equal(source.Signature, dump.Signature);
        Assert.True(dump.IsAmiibo);
        Assert.True(dump.IsLocked);
    }

    [Fact]
    public async Task ReadTag_UsesFastReadInBatches()
    {
        var tag = TestData.LockedAmiiboTag();
        await AmiiboNfcOperations.ReadTagAsync(new SimulatedTagConnection(tag, maxFastReadPages: 32));

        var fastReads = tag.CommandLog.Where(c => c[0] == Ntag215Simulator.CmdFastRead).ToList();
        Assert.Equal(5, fastReads.Count); // 135 pages in batches of 32.
        Assert.All(fastReads, c => Assert.True(c[2] - c[1] < 32));
        Assert.Contains(tag.CommandLog, c => c[0] == Ntag215Simulator.CmdGetVersion);
        Assert.Contains(tag.CommandLog, c => c[0] == Ntag215Simulator.CmdReadSig);
    }

    [Fact]
    public async Task ReadTag_FallsBackToReadWhenFastReadIsUnavailable()
    {
        var source = TestData.Amiibo();
        var tag = Ntag215Simulator.FromAmiiboDump(source);
        var connection = new SimulatedTagConnection(tag, unsupportedCommands: [Ntag215Simulator.CmdFastRead]);

        var dump = await AmiiboNfcOperations.ReadTagAsync(connection);

        Assert.Equal(source.Span[..532].ToArray(), dump.Span[..532].ToArray());
        Assert.DoesNotContain(tag.CommandLog, c => c[0] == Ntag215Simulator.CmdFastRead);
    }

    [Fact]
    public async Task ReadTag_WithoutGetVersionOrReadSig_StillReadsData()
    {
        var source = TestData.Amiibo();
        var tag = Ntag215Simulator.FromAmiiboDump(source);
        var connection = new SimulatedTagConnection(tag, unsupportedCommands: [Ntag215Simulator.CmdGetVersion, Ntag215Simulator.CmdReadSig]);

        var dump = await AmiiboNfcOperations.ReadTagAsync(connection);

        Assert.Equal(source.Span[..532].ToArray(), dump.Span[..532].ToArray());
        Assert.False(dump.HasSignature);
    }

    [Fact]
    public async Task ReadTag_RejectsOtherTagTypes()
    {
        var tag = Ntag215Simulator.CreateBlank();
        tag.Version = [0x00, 0x04, 0x04, 0x02, 0x01, 0x00, 0x0F, 0x03]; // NTAG213

        await Assert.ThrowsAsync<NfcException>(() => AmiiboNfcOperations.ReadTagAsync(new SimulatedTagConnection(tag)));
    }

    [Fact]
    public async Task WriteTag_BlankTag_WritesAndLocksExactlyLikeAnAmiibo()
    {
        var keys = TestData.Keys;
        var source = TestData.Amiibo(TestData.LinkId, uidSeed: 0x40);
        var tag = Ntag215Simulator.CreateBlank(TestData.Uid7(0x55));

        var result = await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), source, keys);

        Assert.Equal(NfcWriteKind.FullWrite, result);

        // Pages 0-132 match the source re-encrypted for this tag's UID.
        var expected = source.Patch(TestData.Uid9(0x55), keys);
        Assert.Equal(expected.Span[..532].ToArray(), tag.Memory.AsSpan(0, 532).ToArray());

        // PWD is derived from the UID, PACK is 80 80.
        Assert.Equal(NtagUid.AmiiboPassword(TestData.Uid9(0x55)), tag.GetPage(Ntag215Simulator.PwdPage));
        Assert.Equal(new byte[] { 0x80, 0x80, 0x00, 0x00 }, tag.GetPage(Ntag215Simulator.PackPage));

        // The data decrypts back to the source's plain text.
        var written = new TagDump(tag.Memory);
        Assert.True(written.IsAmiibo);
        Assert.True(written.IsLocked);
        Assert.Equal(source.Decrypt(keys), written.Decrypt(keys));
        Assert.Equal(source.AmiiboIdHex, written.AmiiboIdHex);
    }

    [Fact]
    public async Task WriteTag_BlankTag_ResultIsLockedAndPasswordProtected()
    {
        var keys = TestData.Keys;
        var tag = Ntag215Simulator.CreateBlank(TestData.Uid7(0x55));
        await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(), keys);

        tag.PowerCycle();
        var connection = new SimulatedTagConnection(tag);
        var client = new Ntag215Client(connection);

        // Without the password nothing at or after AUTH0 (page 4) can be written.
        await Assert.ThrowsAsync<NfcException>(() => client.WritePageAsync(4, new byte[4]));

        tag.Activate();
        Assert.Equal(new byte[] { 0x80, 0x80 }, await client.PasswordAuthAsync(NtagUid.AmiiboPassword(TestData.Uid9(0x55))));

        // Statically locked pages 13-15, dynamically locked pages 16-31 and the config stay read-only even when authenticated.
        foreach (var page in new[] { 13, 15, 16, 21, 31, Ntag215Simulator.Cfg0Page, Ntag215Simulator.Cfg1Page })
        {
            await Assert.ThrowsAsync<NfcException>(() => client.WritePageAsync(page, new byte[4]));
            tag.Activate();
            await client.PasswordAuthAsync(NtagUid.AmiiboPassword(TestData.Uid9(0x55)));
        }

        // App data pages stay writable after authentication.
        await client.WritePageAsync(4, new byte[] { 1, 2, 3, 4 });
        await client.WritePageAsync(32, new byte[] { 1, 2, 3, 4 });
        await client.WritePageAsync(129, new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task WriteTag_BlankTag_IssuesTheAmiiboWriteSequence()
    {
        var tag = Ntag215Simulator.CreateBlank(TestData.Uid7(0x55));
        await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(), TestData.Keys);

        var writes = tag.CommandLog.Where(c => c[0] == Ntag215Simulator.CmdWrite).Select(c => (int)c[1]).ToList();
        var dataPages = Enumerable.Range(4, 126).ToList();
        Assert.Equal(dataPages, writes.Take(126).ToList());
        Assert.Equal(new[] { 3, 134, 133, 131, 132, 130, 2 }, writes.Skip(126).ToList()); // Protection last, static lock bits very last.
    }

    [Fact]
    public async Task WriteTag_BlankTagWithoutKeys_Throws()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var before = tag.Memory;

        var error = await Assert.ThrowsAsync<NfcException>(() =>
            AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(), keys: null));

        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, tag.Memory);
    }

    [Fact]
    public async Task WriteTag_SameAmiibo_RewritesOnlyAppData()
    {
        var keys = TestData.Keys;
        var original = TestData.Amiibo(uidSeed: 0x10);
        var tag = Ntag215Simulator.FromAmiiboDump(original);
        var before = tag.Memory;

        // Same figure on another UID: it's re-encrypted for this tag and only the writable app data is replaced.
        var source = TestData.Amiibo(uidSeed: 0x70);
        var result = await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), source, keys);

        Assert.Equal(NfcWriteKind.AppData, result);
        // Decrypted from the source, re-encrypted for the target's UID (and its locked salt).
        var expected = AmiiboCrypto.FromInternal(
            AmiiboCrypto.RetargetAppData(
                AmiiboCrypto.ToInternal(source.Span, AmiiboCrypto.Ntag215Layout),
                AmiiboCrypto.ToInternal(original.Span, AmiiboCrypto.Ntag215Layout),
                keys),
            original.ToArray(TagDump.DataLength),
            AmiiboCrypto.Ntag215Layout);
        var after = tag.Memory;

        for (var page = 0; page < Ntag215Simulator.PageCount; page++)
        {
            var written = page is >= 4 and <= 12 or >= 32 and <= 129;
            var expectedPage = written ? expected.AsSpan(page * 4, 4).ToArray() : before.AsSpan(page * 4, 4).ToArray();
            Assert.True(expectedPage.AsSpan().SequenceEqual(after.AsSpan(page * 4, 4)), $"Page {page} is wrong.");
        }

        Assert.Contains(tag.CommandLog, c => c[0] == Ntag215Simulator.CmdPwdAuth);
    }

    [Fact]
    public async Task WriteTag_SameTagAndUid_WritesTheDumpWithoutKeys()
    {
        var dump = TestData.Amiibo(uidSeed: 0x10);
        var tag = Ntag215Simulator.FromAmiiboDump(dump);

        var result = await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), dump, keys: null);

        Assert.Equal(NfcWriteKind.AppData, result);
        Assert.Equal(dump.Span[..532].ToArray(), tag.Memory.AsSpan(0, 532).ToArray());
    }

    [Fact]
    public async Task WriteTag_DifferentCharacter_IsRefused()
    {
        var tag = TestData.LockedAmiiboTag(TestData.MarioId);
        var before = tag.Memory;

        await Assert.ThrowsAsync<NfcException>(() =>
            AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(TestData.LinkId), TestData.Keys));

        Assert.Equal(before, tag.Memory);
    }

    [Fact]
    public async Task WriteTag_LockedNonAmiibo_IsRefused()
    {
        var tag = Ntag215Simulator.CreateBlank();
        await tag.WriteAsync(2, [0, 0, 0xFF, 0xFF]);
        tag.Activate();

        var error = await Assert.ThrowsAsync<NfcException>(() =>
            AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(), TestData.Keys));
        Assert.Contains("static lock bits", error.Message);
    }

    [Fact]
    public async Task WriteTag_OnlyDynamicLockBitsSet_CountsAsLocked()
    {
        var tag = Ntag215Simulator.CreateBlank();
        Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xA2, 130, 0x01, 0x00, 0x00, 0x00]).Kind);
        tag.Activate();

        var error = await Assert.ThrowsAsync<NfcException>(() =>
            AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(), TestData.Keys));
        Assert.Contains("dynamic lock bits", error.Message);
        Assert.DoesNotContain(tag.CommandLog, c => c[0] == Ntag215Simulator.CmdWrite && c[1] != 130);
    }

    [Fact]
    public async Task WriteTag_PasswordProtectedNonAmiibo_IsRefused()
    {
        var tag = Ntag215Simulator.CreateBlank();
        Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xA2, Ntag215Simulator.Cfg0Page, 0x04, 0x00, 0x00, 0x10]).Kind);
        tag.Activate();

        var error = await Assert.ThrowsAsync<NfcException>(() =>
            AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(), TestData.Keys));
        Assert.Contains("password protected", error.Message);
    }

    [Fact]
    public async Task WriteTag_UnlockedAmiibo_GetsAFullWriteWithLocksLast()
    {
        // Amiibo data that was never locked (e.g. an interrupted write): rewritten whole, then locked.
        var memory = TestData.Amiibo(uidSeed: 0x66).ToArray(TagDump.DataLength);
        memory[10] = 0;
        memory[11] = 0;
        new byte[] { 0x00, 0x00, 0x00, 0xBD }.CopyTo(memory, 520);
        new byte[] { 0x04, 0x00, 0x00, 0xFF, 0x00, 0x05, 0x00, 0x00 }.CopyTo(memory, 524);
        var tag = Ntag215Simulator.FromMemory(memory);

        var result = await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(), TestData.Keys);

        Assert.Equal(NfcWriteKind.FullWrite, result);
        var writes = tag.CommandLog.Where(c => c[0] == Ntag215Simulator.CmdWrite).Select(c => (int)c[1]).ToList();
        Assert.Equal(2, writes[^1]);
        Assert.True(tag.IsPageLocked(16));
    }

    [Fact]
    public async Task WriteTag_LockedAmiibo_OnlyGetsAppData()
    {
        var tag = TestData.LockedAmiiboTag(uidSeed: 0x10);
        var result = await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), TestData.Amiibo(uidSeed: 0x70), TestData.Keys);

        Assert.Equal(NfcWriteKind.AppData, result);
        var writes = tag.CommandLog.Where(c => c[0] == Ntag215Simulator.CmdWrite).Select(c => (int)c[1]).ToList();
        Assert.All(writes, page => Assert.True(page is >= 4 and <= 12 or >= 32 and <= 129, $"Page {page} isn't app data."));
    }

    [Fact]
    public async Task WriteTag_Puck_WritesRawPagesAndSaves()
    {
        var puck = new PuckNfcSimulator();
        var dump = TestData.Amiibo();

        var result = await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(puck), dump, keys: null);

        Assert.Equal(NfcWriteKind.Puck, result);
        Assert.Equal(dump.ToArray(TagDump.FullLength), puck.Memory);
        Assert.True(puck.Saved);
    }

    [Fact]
    public async Task IsPuck_IsFalseForARealAmiibo()
    {
        Assert.False(await AmiiboNfcOperations.IsPuckAsync(new SimulatedTagConnection(TestData.LockedAmiiboTag())));
    }

    [Fact]
    public async Task WriteTag_TagRemovedMidWrite_Throws()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var connection = new RemovingConnection(tag, removeAfterWrites: 10);

        await Assert.ThrowsAsync<NfcException>(() => AmiiboNfcOperations.WriteTagAsync(connection, TestData.Amiibo(), TestData.Keys));
    }

    /// <summary>
    /// Pulls the tag out of the field after a number of writes.
    /// </summary>
    private sealed class RemovingConnection(TagSimulator tag, int removeAfterWrites) : INfcTagConnection
    {
        private readonly SimulatedTagConnection _inner = new(tag);
        private int _writes;

        public byte[] Uid => _inner.Uid;

        public int MaxFastReadPages => _inner.MaxFastReadPages;

        public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
        {
            if (command[0] == Ntag215Simulator.CmdWrite && ++_writes > removeAfterWrites)
            {
                tag.Halt();
            }

            return _inner.TransceiveAsync(command, cancellationToken);
        }

        public void SetStatus(string message)
        {
        }

        public Task CloseAsync(string? errorMessage = null) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class TagSimulatorTestExtensions
{
    /// <summary>
    /// Writes a page directly through the command interface (test setup helper).
    /// </summary>
    public static Task WriteAsync(this TagSimulator tag, int page, byte[] data)
    {
        var response = tag.Transceive([Ntag215Simulator.CmdWrite, (byte)page, .. data]);
        Assert.Equal(TagResponseKind.Ack, response.Kind);
        return Task.CompletedTask;
    }
}
