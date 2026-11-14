using System.Text;
using AmiiTag.Nfc;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;
using Xunit.Abstractions;

namespace AmiiTag.Tests;

/// <summary>
/// SHA-256 of retail key output for the deterministic fixtures in <see cref="RetailKeyFixtures"/>.
/// Encrypted data is never stored, only its hash. Empty entries haven't been recorded yet: run
/// <see cref="RetailKeyTests.PrintGoldenHashes"/> once with the real key file and paste its output here.
/// </summary>
public static class RetailKeyGoldenHashes
{
    private static readonly Dictionary<string, string> Hashes = new()
    {
        ["v2.encrypted"] = "e7d9d0905e632e3b23bbabc8b1d4671c879c5d48b27e838ea448d1e3a8af73de",
        ["v3.encrypted"] = "6dabf477c8959403f76a766ea676f89627a3632d99167f34740b2bd5a4af77f2",
        ["v2.retargeted"] = "f5cf76d3f9a0b24b8b1c62e71c39e2c984d3f7475fd18f2bb956e48e80b66002",
        ["v3.retargeted"] = "c05710183f7c2fa09a3d53327b169962bf8623258f87de25e5ceb5557a264550",
        ["v3.newUid"] = "5b755c45b4af65bb820933a763299294a2d7e44f7cda408157ff25063e831aff",
    };

    public static IReadOnlyCollection<string> Names => Hashes.Keys;

    public static string? Get(string name) => Hashes.TryGetValue(name, out var hash) ? hash : null;
}

/// <summary>
/// Deterministic amiibo built with the retail keys: fixed UIDs, keygen salts, nickname and app data.
/// </summary>
public static class RetailKeyFixtures
{
    public const string V2Id = TestData.MarioId;
    public const string V3Id = AmiiboV3Tests.V3Id;

    public static readonly byte[] UidA = [0x04, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
    public static readonly byte[] UidB = [0x04, 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6];

    /// <summary>
    /// Sets the "settings initialised" flag, a nickname and recognisable app data in plain internal data.
    /// </summary>
    public static byte[] WithUserData(byte[] plain, string nickname)
    {
        var data = (byte[])plain.Clone();
        data[0x02E] = 0x00;
        data[0x02F] = 0x01;
        Array.Clear(data, 0x038, 20);
        Encoding.BigEndianUnicode.GetBytes(nickname).CopyTo(data, 0x038);
        var app = RetailKeys.Bytes32("AmiiTag app data " + nickname);
        for (var i = 0x0DC; i < 0x1B4; i++)
        {
            data[i] = app[i % 32];
        }

        return data;
    }

    /// <summary>
    /// An NTAG215 amiibo: plain 532 byte image, then encrypted with the retail keys.
    /// </summary>
    public static (byte[] Plain, TagDump Encrypted) V2(byte[] uid7, string saltLabel, string nickname)
    {
        var raw = TagDump.FromAmiiboId(V2Id, keys: null).Data;
        NtagUid.FromSevenByteUid(uid7).CopyTo(raw, 0);
        RetailKeys.Bytes32(saltLabel).CopyTo(raw, 0x060);

        var plain = WithUserData(AmiiboCrypto.ToInternal(raw, AmiiboCrypto.Ntag215Layout), nickname);
        var plainRaw = AmiiboCrypto.FromInternal(plain, raw, AmiiboCrypto.Ntag215Layout);
        var cipher = AmiiboCrypto.Encrypt(plain, RetailKeys.Required);
        return (plainRaw, new TagDump(AmiiboCrypto.FromInternal(cipher, raw, AmiiboCrypto.Ntag215Layout)));
    }

    /// <summary>
    /// A v3 amiibo from AmiiboZero's template with a fixed salt, encrypted with the retail keys.
    /// </summary>
    public static AmiiboV3Dump V3(byte[] uid7, string saltLabel, string nickname, byte[]? lockOn = null)
    {
        var raw = AmiiboV3Dump.Create(Hex.FromHex(V3Id), keys: null, uid7, lockOn).Data;
        RetailKeys.Bytes32(saltLabel).CopyTo(raw, 0x060);
        var template = new AmiiboV3Dump(raw);
        return template.WithPlainInternal(WithUserData(template.ToInternal(), nickname), RetailKeys.Required);
    }

    public static (string Name, string Hash)[] ComputeGoldenHashes()
    {
        var keys = RetailKeys.Required;
        var (_, v2A) = V2(UidA, "salt A", "Retail A");
        var (_, v2B) = V2(UidB, "salt B", "Retail B");
        var v3A = V3(UidA, "salt A", "Retail A");
        var v3B = V3(UidB, "salt B", "Retail B");

        return
        [
            ("v2.encrypted", RetailKeys.Sha256(v2A.Span[..0x208])),
            ("v3.encrypted", RetailKeys.Sha256(v3A.Span)),
            ("v2.retargeted", RetailKeys.Sha256(AmiiboCrypto.RetargetAppData(
                AmiiboCrypto.ToInternal(v2A.Span, AmiiboCrypto.Ntag215Layout),
                AmiiboCrypto.ToInternal(v2B.Span, AmiiboCrypto.Ntag215Layout),
                keys))),
            ("v3.retargeted", RetailKeys.Sha256(AmiiboCrypto.RetargetAppData(v3A.ToInternal(), v3B.ToInternal(), keys))),
            ("v3.newUid", RetailKeys.Sha256(v3A.WithUid(keys, UidB).Span)),
        ];
    }
}

/// <summary>
/// Tests with the real key_retail.bin, skipped when it isn't available. Expected encrypted output is compared by hash.
/// </summary>
public class RetailKeyTests
{
    private readonly ITestOutputHelper _output;

    public RetailKeyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static AmiiboKeySet Keys => RetailKeys.Required;

    private static void AssertGolden(string name)
    {
        var expected = RetailKeyGoldenHashes.Get(name);
        var actual = RetailKeyFixtures.ComputeGoldenHashes().Single(h => h.Name == name).Hash;
        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------- Recording

    /// <summary>
    /// Prints the hashes for <see cref="RetailKeyGoldenHashes"/>. Check the consistency tests pass first.
    /// </summary>
    [RetailKeysFact]
    public void PrintGoldenHashes()
    {
        foreach (var (name, hash) in RetailKeyFixtures.ComputeGoldenHashes())
        {
            _output.WriteLine($"[\"{name}\"] = \"{hash}\",");
        }

        Assert.Equal(RetailKeyGoldenHashes.Names.Order(), RetailKeyFixtures.ComputeGoldenHashes().Select(h => h.Name).Order());
    }

    [RetailKeysFact]
    public void Fixtures_AreDeterministic()
    {
        Assert.Equal(RetailKeyFixtures.ComputeGoldenHashes(), RetailKeyFixtures.ComputeGoldenHashes());
    }

    // ---------------------------------------------------------------- Golden hashes

    [RetailKeysGoldenFact("v2.encrypted")]
    public void V2_EncryptedImage_MatchesTheRecordedHash() => AssertGolden("v2.encrypted");

    [RetailKeysGoldenFact("v3.encrypted")]
    public void V3_EncryptedImage_MatchesTheRecordedHash() => AssertGolden("v3.encrypted");

    [RetailKeysGoldenFact("v2.retargeted")]
    public void V2_RetargetedAppData_MatchesTheRecordedHash() => AssertGolden("v2.retargeted");

    [RetailKeysGoldenFact("v3.retargeted")]
    public void V3_RetargetedAppData_MatchesTheRecordedHash() => AssertGolden("v3.retargeted");

    [RetailKeysGoldenFact("v3.newUid")]
    public void V3_NewUid_MatchesTheRecordedHash() => AssertGolden("v3.newUid");

    // ---------------------------------------------------------------- Consistency (no recorded values needed)

    [RetailKeysFact]
    public void KeyFile_IsTheRetailOne()
    {
        Assert.NotNull(RetailKeys.Keys);
    }

    [RetailKeysFact]
    public void V2_SharedCryptoMatchesTheTagDumpImplementation()
    {
        var (plainRaw, encrypted) = RetailKeyFixtures.V2(RetailKeyFixtures.UidA, "salt A", "Retail A");

        // TagDump.Patch is an independent implementation of the same encryption.
        var legacy = new TagDump(plainRaw).Patch(NtagUid.FromSevenByteUid(RetailKeyFixtures.UidA), Keys, skipDecrypt: true);
        Assert.Equal(RetailKeys.Sha256(legacy.Span[..0x208]), RetailKeys.Sha256(encrypted.Span[..0x208]));

        var plain = AmiiboCrypto.Decrypt(AmiiboCrypto.ToInternal(encrypted.Span, AmiiboCrypto.Ntag215Layout), Keys);
        Assert.Equal(RetailKeys.Sha256(encrypted.Decrypt(Keys)), RetailKeys.Sha256(plain.AsSpan(AmiiboCrypto.EncryptedStart, AmiiboCrypto.EncryptedLength)));
        Assert.Equal("Retail A", encrypted.GetNickname(Keys));
    }

    [RetailKeysFact]
    public void V2_EncryptDecrypt_RoundTrips_AndAuthenticates()
    {
        var (plainRaw, encrypted) = RetailKeyFixtures.V2(RetailKeyFixtures.UidA, "salt A", "Retail A");
        var internalData = AmiiboCrypto.ToInternal(encrypted.Span, AmiiboCrypto.Ntag215Layout);

        Assert.True(AmiiboCrypto.Authenticate(internalData, Keys));

        // Decrypting gives back the settings and app data exactly (the signature fields are only filled in on encryption).
        var encryptedRange = AmiiboCrypto.EncryptedStart..(AmiiboCrypto.EncryptedStart + AmiiboCrypto.EncryptedLength);
        Assert.Equal(
            RetailKeys.Sha256(AmiiboCrypto.ToInternal(plainRaw, AmiiboCrypto.Ntag215Layout)[encryptedRange]),
            RetailKeys.Sha256(AmiiboCrypto.Decrypt(internalData, Keys)[encryptedRange]));

        var tampered = (byte[])internalData.Clone();
        tampered[0x100] ^= 0x01;
        Assert.False(AmiiboCrypto.Authenticate(tampered, Keys));
    }

    [RetailKeysFact]
    public void V3_UsesTheSameCryptoAsV2_OnlyTheLayoutDiffers()
    {
        var (_, v2) = RetailKeyFixtures.V2(RetailKeyFixtures.UidA, "salt A", "Retail A");
        var v2Internal = AmiiboCrypto.ToInternal(v2.Span, AmiiboCrypto.Ntag215Layout);

        // Put the same internal data into a v3 image: it must authenticate there as well.
        var v3 = new AmiiboV3Dump(AmiiboCrypto.FromInternal(v2Internal, new byte[AmiiboV3Dump.ImageLength], AmiiboCrypto.NtagI2cPlusLayout));
        Assert.True(v3.Authenticate(Keys));
        Assert.Equal(RetailKeys.Sha256(v2Internal), RetailKeys.Sha256(v3.ToInternal()));

        // Read directly: GetNickname only answers for v3 IDs (ending 03), and this one is the v2 figure's.
        Assert.Equal("Retail A", AmiiboCrypto.ReadNickname(v3.Decrypt(Keys)));
    }

    [RetailKeysFact]
    public void V3_Generated_Authenticates_WithAndWithoutLockOn()
    {
        var v3 = RetailKeyFixtures.V3(RetailKeyFixtures.UidA, "salt A", "Retail A");
        Assert.True(v3.Authenticate(Keys));
        Assert.Equal("Retail A", v3.GetNickname(Keys));

        var withLockOn = v3.WithLockOn(new byte[] { 1, 2, 3 });
        Assert.True(withLockOn.Authenticate(Keys));
        Assert.Equal(RetailKeys.Sha256(v3.ToInternal()), RetailKeys.Sha256(withLockOn.ToInternal()));

        var random = AmiiboV3Dump.Create(RetailKeyFixtures.V3Id, Keys);
        Assert.True(random.Authenticate(Keys));
    }

    [RetailKeysFact]
    public void V3_NewUid_KeepsTheUserData()
    {
        var v3 = RetailKeyFixtures.V3(RetailKeyFixtures.UidA, "salt A", "Retail A", lockOn: [9, 9, 9]);
        var moved = v3.WithUid(Keys, RetailKeyFixtures.UidB);

        Assert.True(moved.Authenticate(Keys));
        Assert.Equal(RetailKeyFixtures.UidB, moved.Uid);
        Assert.Equal("Retail A", moved.GetNickname(Keys));
        Assert.Equal(v3.LockOn, moved.LockOn);
        Assert.Equal(
            RetailKeys.Sha256(v3.Decrypt(Keys).AsSpan(AmiiboCrypto.EncryptedStart, AmiiboCrypto.EncryptedLength)),
            RetailKeys.Sha256(moved.Decrypt(Keys).AsSpan(AmiiboCrypto.EncryptedStart, AmiiboCrypto.EncryptedLength)));
    }

    [RetailKeysFact]
    public void Retarget_IsValidOnlyOnTheTarget()
    {
        var source = RetailKeyFixtures.V3(RetailKeyFixtures.UidA, "salt A", "Retail A");
        var target = RetailKeyFixtures.V3(RetailKeyFixtures.UidB, "salt B", "Retail B");

        var result = AmiiboCrypto.RetargetAppData(source.ToInternal(), target.ToInternal(), Keys);

        Assert.True(AmiiboCrypto.Authenticate(result, Keys));
        Assert.Equal("Retail A", AmiiboCrypto.ReadNickname(AmiiboCrypto.Decrypt(result, Keys)));
        Assert.Equal(RetailKeys.Sha256(target.ToInternal().AsSpan(0x1B4)), RetailKeys.Sha256(result.AsSpan(0x1B4))); // Tag HMAC, UID, ID, salt.

        var onSource = (byte[])result.Clone();
        source.ToInternal().AsSpan(0x1D4).CopyTo(onSource.AsSpan(0x1D4));
        Assert.False(AmiiboCrypto.Authenticate(onSource, Keys));
    }

    // ---------------------------------------------------------------- End to end over simulated NFC

    [RetailKeysFact]
    public async Task Ntag215_AppDataWrite_ToAnotherFigure_ReadsBackValid()
    {
        var (_, source) = RetailKeyFixtures.V2(RetailKeyFixtures.UidA, "salt A", "Retail A");
        var (_, target) = RetailKeyFixtures.V2(RetailKeyFixtures.UidB, "salt B", "Retail B");
        var tag = Ntag215Simulator.FromAmiiboDump(target);

        Assert.Equal(NfcWriteKind.AppData, await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), source, Keys));

        tag.Activate();
        var read = await AmiiboNfcOperations.ReadTagAsync(new SimulatedTagConnection(tag));
        Assert.True(AmiiboCrypto.Authenticate(AmiiboCrypto.ToInternal(read.Span, AmiiboCrypto.Ntag215Layout), Keys));
        Assert.Equal("Retail A", read.GetNickname(Keys));
        Assert.Equal(target.Uid, read.Uid);
        Assert.Equal(RetailKeys.Sha256(target.Span[0x34..0x80]), RetailKeys.Sha256(read.Span[0x34..0x80]));
    }

    [RetailKeysFact]
    public async Task Ntag215_BlankWrite_ReadsBackValid()
    {
        var (_, source) = RetailKeyFixtures.V2(RetailKeyFixtures.UidA, "salt A", "Retail A");
        var tag = Ntag215Simulator.CreateBlank(RetailKeyFixtures.UidB);

        Assert.Equal(NfcWriteKind.FullWrite, await AmiiboNfcOperations.WriteTagAsync(new SimulatedTagConnection(tag), source, Keys));

        tag.PowerCycle();
        var read = await AmiiboNfcOperations.ReadTagAsync(new SimulatedTagConnection(tag));
        Assert.True(read.IsLocked);
        Assert.True(AmiiboCrypto.Authenticate(AmiiboCrypto.ToInternal(read.Span, AmiiboCrypto.Ntag215Layout), Keys));
        Assert.Equal("Retail A", read.GetNickname(Keys));
    }

    [RetailKeysFact]
    public async Task V3_AppDataWrite_ToAnotherFigure_ReadsBackValid()
    {
        var source = RetailKeyFixtures.V3(RetailKeyFixtures.UidA, "salt A", "Retail A");
        var targetImage = RetailKeyFixtures.V3(RetailKeyFixtures.UidB, "salt B", "Retail B", lockOn: [7, 7, 7]);
        var raw = targetImage.Data;
        NtagUid.AmiiboPassword(NtagUid.FromSevenByteUid(RetailKeyFixtures.UidB)).CopyTo(raw, 0xE5 * 4);
        var tag = new NtagI2cPlusSimulator(raw);

        await using (var connection = new SimulatedTagConnection(tag))
        {
            Assert.Equal(NfcWriteKind.AppData, await AmiiboV3NfcOperations.WriteAppDataAsync(connection, source, Keys));
        }

        tag.Activate();
        await using var reader = new SimulatedTagConnection(tag);
        var read = await AmiiboV3NfcOperations.ReadV3Async(reader);

        Assert.True(read.Authenticate(Keys));
        Assert.Equal("Retail A", read.GetNickname(Keys));
        Assert.Equal(RetailKeyFixtures.UidB, read.Uid);
        Assert.Equal(targetImage.LockOn, read.LockOn);
        Assert.Equal(RetailKeys.Sha256(targetImage.Span.Slice(0x034, 0x4C)), RetailKeys.Sha256(read.Span.Slice(0x034, 0x4C)));
    }
}
