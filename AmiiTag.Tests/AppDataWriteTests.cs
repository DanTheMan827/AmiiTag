using System.Text;
using AmiiTag.Nfc;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// Writing app data to an existing figure: decrypt the source, re-encrypt for the target's UID (and its locked
/// salt), and only write the header and encrypted data pages. v3 figures can only be written this way.
/// </summary>
public class AppDataWriteTests
{
    private static readonly byte[] UidA = [0x04, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
    private static readonly byte[] UidB = [0x04, 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6];

    private static AmiiboV3Dump V3(byte[] uid, string id = AmiiboV3Tests.V3Id, byte[]? lockOn = null) =>
        AmiiboV3Dump.Create(Hex.FromHex(id), TestData.Keys, uid, lockOn);

    private static byte[] Named(byte[] internalCipher, string name)
    {
        var plain = AmiiboCrypto.Decrypt(internalCipher, TestData.Keys);
        plain[0x02E] = 0x00;
        plain[0x02F] = 0x01;
        Array.Clear(plain, 0x038, 20);
        Encoding.BigEndianUnicode.GetBytes(name).CopyTo(plain, 0x038);
        plain[0x0DC] = 0x5A; // Some app data.
        return AmiiboCrypto.Encrypt(plain, TestData.Keys);
    }

    private static AmiiboV3Dump Named(AmiiboV3Dump figure, string name) =>
        new(AmiiboCrypto.FromInternal(Named(figure.ToInternal(), name), figure.Span, AmiiboCrypto.NtagI2cPlusLayout));

    /// <summary>
    /// A "real" v3 figure: PWD/PACK set from the UID like an NTAG215 amiibo.
    /// </summary>
    private static NtagI2cPlusSimulator RealFigure(AmiiboV3Dump figure)
    {
        var raw = figure.Data;
        NtagUid.AmiiboPassword(NtagUid.FromSevenByteUid(figure.Uid)).CopyTo(raw, AmiiboV3Dump.PwdPage * 4);
        raw[AmiiboV3Dump.PackPage * 4] = 0x80;
        raw[AmiiboV3Dump.PackPage * 4 + 1] = 0x80;
        return new NtagI2cPlusSimulator(raw);
    }

    // ---------------------------------------------------------------- Crypto

    [Fact]
    public void Retarget_UsesTheTargetsUidAndSaltAndKeepsTheSourceData()
    {
        var source = Named(V3(UidA), "Traveller");
        var target = V3(UidB);

        var result = AmiiboCrypto.RetargetAppData(source.ToInternal(), target.ToInternal(), TestData.Keys);

        Assert.True(AmiiboCrypto.Authenticate(result, TestData.Keys));
        var plain = AmiiboCrypto.Decrypt(result, TestData.Keys);
        Assert.Equal("Traveller", AmiiboCrypto.ReadNickname(plain));
        Assert.Equal(0x5A, plain[0x0DC]);
        Assert.Equal(target.ToInternal()[0x1D4..], result[0x1D4..]); // UID, ID, salt.
        Assert.Equal(target.ToInternal()[0x1B4..0x1D4], result[0x1B4..0x1D4]); // Tag HMAC already locked on the target.
        var onSourceTag = result.ToArray();
        source.ToInternal().AsSpan(0x1D4).CopyTo(onSourceTag.AsSpan(0x1D4));
        Assert.False(AmiiboCrypto.Authenticate(onSourceTag, TestData.Keys)); // Only valid on the target.
    }

    [Fact]
    public void Retarget_RefusesADifferentCharacter()
    {
        var source = V3(UidA);
        var target = V3(UidB, "0000000000350103");
        Assert.Throws<TagException>(() => AmiiboCrypto.RetargetAppData(source.ToInternal(), target.ToInternal(), TestData.Keys));
    }

    // ---------------------------------------------------------------- v3 over NFC

    [Fact]
    public async Task V3_AppDataIsReEncryptedForAnotherFigure()
    {
        var source = Named(V3(UidA), "Traveller");
        var targetImage = V3(UidB, lockOn: Enumerable.Range(1, 62).Select(i => (byte)i).ToArray());
        var tag = RealFigure(targetImage);
        var before = tag.GetPage(0, 0x15);

        await using (var connection = new SimulatedTagConnection(tag))
        {
            Assert.Equal(NfcWriteKind.AppData, await AmiiboV3NfcOperations.WriteAppDataAsync(connection, source, TestData.Keys));
        }

        Assert.Contains(tag.CommandLog, c => c[0] == NtagI2cPlusSimulator.CmdPwdAuth);
        Assert.Equal(before, tag.GetPage(0, 0x15));

        tag.Activate();
        await using var reader = new SimulatedTagConnection(tag);
        var read = await AmiiboV3NfcOperations.ReadV3Async(reader);

        Assert.Equal(UidB, read.Uid);
        Assert.True(read.Authenticate(TestData.Keys));
        Assert.Equal("Traveller", read.GetNickname(TestData.Keys));
        Assert.Equal(targetImage.Span.Slice(0x034, 0x4C).ToArray(), read.Span.Slice(0x034, 0x4C).ToArray()); // Tag HMAC, ID, salt.
        Assert.Equal(targetImage.LockOn, read.LockOn);
        Assert.Equal(targetImage.Span[0x080..0x0C0].ToArray(), read.Span[0x080..0x0C0].ToArray());
    }

    [Fact]
    public async Task V3_SameFigure_NeedsNoKeys()
    {
        var figure = V3(UidB);
        var tag = RealFigure(figure);
        var source = Named(figure, "Again");

        await using var connection = new SimulatedTagConnection(tag);
        Assert.Equal(NfcWriteKind.AppData, await AmiiboV3NfcOperations.WriteAppDataAsync(connection, source, keys: null));
        Assert.Equal(source.GetPage(0, 0x40), tag.GetPage(0, 0x40));
    }

    [Fact]
    public async Task V3_DifferentFigure_NeedsTheKeys()
    {
        var tag = RealFigure(V3(UidB));
        await using var connection = new SimulatedTagConnection(tag);

        var error = await Assert.ThrowsAsync<NfcException>(() => AmiiboV3NfcOperations.WriteAppDataAsync(connection, V3(UidA), keys: null));
        Assert.Contains("key file", error.Message);
        Assert.DoesNotContain(tag.CommandLog, c => c[0] == NtagI2cPlusSimulator.CmdWrite);
    }

    [Fact]
    public async Task V3_CantBeWrittenToABlankTag()
    {
        var raw = V3(UidB).Data;
        Array.Clear(raw, 0x054, 8);
        var tag = new NtagI2cPlusSimulator(raw);
        await using var connection = new SimulatedTagConnection(tag);

        var error = await Assert.ThrowsAsync<NfcException>(() => AmiiboV3NfcOperations.WriteAppDataAsync(connection, V3(UidA), TestData.Keys));
        Assert.Contains("blank", error.Message);
        Assert.DoesNotContain(tag.CommandLog, c => c[0] == NtagI2cPlusSimulator.CmdWrite);
    }

    [Fact]
    public async Task V3_CantBeWrittenToAnNtag215()
    {
        var tag = Ntag215Simulator.CreateBlank();
        await using var connection = new SimulatedTagConnection(tag);

        var error = await Assert.ThrowsAsync<NfcException>(() => AmiiboV3NfcOperations.WriteAppDataAsync(connection, V3(UidA), TestData.Keys));
        Assert.Contains("v3 amiibo figure", error.Message);
        Assert.DoesNotContain(tag.CommandLog, c => c[0] == Ntag215Simulator.CmdWrite);
    }

    [Fact]
    public async Task V3_DifferentCharacter_IsRefused()
    {
        var tag = RealFigure(V3(UidB, "0000000000350103"));
        await using var connection = new SimulatedTagConnection(tag);

        await Assert.ThrowsAsync<NfcException>(() => AmiiboV3NfcOperations.WriteAppDataAsync(connection, V3(UidA), TestData.Keys));
        Assert.DoesNotContain(tag.CommandLog, c => c[0] == NtagI2cPlusSimulator.CmdWrite);
    }

    [Fact]
    public async Task V3_WrongPassword_IsReported()
    {
        var tag = new NtagI2cPlusSimulator(V3(UidB).Data.Select((b, i) => i is >= 0x394 and < 0x398 ? (byte)0x99 : b).ToArray());
        await using var connection = new SimulatedTagConnection(tag);

        var error = await Assert.ThrowsAsync<NfcException>(() => AmiiboV3NfcOperations.WriteAppDataAsync(connection, V3(UidA), TestData.Keys));
        Assert.Contains("password", error.Message);
    }

    // ---------------------------------------------------------------- NTAG215 works the same way

    [Fact]
    public async Task Ntag215_AppDataIsReEncryptedForAnotherFigure()
    {
        var source = TestData.Amiibo(uidSeed: 0x10);
        source = new TagDump(AmiiboCrypto.FromInternal(
            Named(AmiiboCrypto.ToInternal(source.Span, AmiiboCrypto.Ntag215Layout), "Hero"),
            source.Span,
            AmiiboCrypto.Ntag215Layout));

        var targetDump = TestData.Amiibo(uidSeed: 0x40);
        var tag = Ntag215Simulator.FromAmiiboDump(targetDump);

        Assert.Equal(NfcWriteKind.AppData, await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), source, TestData.Keys));

        tag.Activate();
        var read = await AmiiboNfcOperations.ReadTagAsync(new SimulatedTagConnection(tag));
        var internalData = AmiiboCrypto.ToInternal(read.Span, AmiiboCrypto.Ntag215Layout);

        Assert.Equal(targetDump.Uid, read.Uid);
        Assert.True(AmiiboCrypto.Authenticate(internalData, TestData.Keys));
        Assert.Equal("Hero", read.GetNickname(TestData.Keys));
        Assert.Equal(targetDump.Span[0x34..0x80].ToArray(), read.Span[0x34..0x80].ToArray()); // Tag HMAC, ID, salt untouched.
    }
}
