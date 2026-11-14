using AmiiTag.Tags;

namespace AmiiTag.Tests;

/// <summary>
/// Recognising key files by size and SHA-256: key_retail.bin, or the split unfixed-info.bin and locked-secret.bin.
/// The made-up test keys have the right layout but not the retail hashes, so they must never be accepted.
/// </summary>
public class KeyFileTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(79)]
    [InlineData(81)]
    [InlineData(159)]
    [InlineData(161)]
    public void WrongSizes_AreNotKeyFiles(int length)
    {
        Assert.Equal(AmiiboKeyFileKind.Unknown, AmiiboKeyFiles.Identify(new byte[length]));
    }

    [Fact]
    public void RightSizesWithUnknownHashes_AreNotKeyFiles()
    {
        var fake = TestData.Keys.Raw;
        Assert.Equal(AmiiboKeyFileKind.Unknown, AmiiboKeyFiles.Identify(fake));
        Assert.Equal(AmiiboKeyFileKind.Unknown, AmiiboKeyFiles.Identify(fake.AsSpan(0, 80)));
        Assert.Equal(AmiiboKeyFileKind.Unknown, AmiiboKeyFiles.Identify(fake.AsSpan(80, 80)));
        Assert.Throws<TagException>(() => AmiiboKeySet.FromUnfixedInfo(fake.AsSpan(0, 80)));
        Assert.Throws<TagException>(() => AmiiboKeySet.FromParts(fake.AsSpan(0, 80), fake.AsSpan(80, 80)));
    }

    [Fact]
    public void KnownHashes_AreSha256Sized()
    {
        Assert.Equal(32, AmiiboKeyFiles.UnfixedInfoHash.Length);
        Assert.Equal(32, AmiiboKeyFiles.LockedSecretHash.Length);
        Assert.NotEqual(AmiiboKeyFiles.UnfixedInfoHash, AmiiboKeyFiles.LockedSecretHash);
    }

    // ---------------------------------------------------------------- With the real key file

    [RetailKeysFact]
    public void Retail_FileAndBothHalves_AreRecognised()
    {
        var raw = RetailKeys.Required.Raw;
        Assert.Equal(AmiiboKeyFileKind.Combined, AmiiboKeyFiles.Identify(raw));
        Assert.Equal(AmiiboKeyFileKind.UnfixedInfo, AmiiboKeyFiles.Identify(raw.AsSpan(0, 80)));
        Assert.Equal(AmiiboKeyFileKind.LockedSecret, AmiiboKeyFiles.Identify(raw.AsSpan(80, 80)));
    }

    [RetailKeysFact]
    public void Retail_HalvesInTheWrongOrder_AreNotAKeyFile()
    {
        var raw = RetailKeys.Required.Raw;
        byte[] swapped = [.. raw.AsSpan(80, 80), .. raw.AsSpan(0, 80)];
        Assert.Equal(AmiiboKeyFileKind.Unknown, AmiiboKeyFiles.Identify(swapped));
        Assert.Throws<TagException>(() => AmiiboKeySet.FromParts(raw.AsSpan(80, 80), raw.AsSpan(0, 80)));
    }

    [RetailKeysFact]
    public void Retail_SplitFiles_GiveTheSameKeysAsKeyRetail()
    {
        var raw = RetailKeys.Required.Raw;
        var fromParts = AmiiboKeySet.FromParts(raw.AsSpan(0, 80), raw.AsSpan(80, 80));

        Assert.True(fromParts.CanEncrypt);
        Assert.Equal(RetailKeys.Sha256(raw), RetailKeys.Sha256(fromParts.Raw));

        var (_, encrypted) = RetailKeyFixtures.V2(RetailKeyFixtures.UidA, "salt A", "Retail A");
        var internalData = AmiiboCrypto.ToInternal(encrypted.Span, AmiiboCrypto.Ntag215Layout);
        Assert.Equal(
            RetailKeys.Sha256(AmiiboCrypto.Encrypt(AmiiboCrypto.Decrypt(internalData, fromParts), fromParts)),
            RetailKeys.Sha256(internalData));
    }

    [RetailKeysFact]
    public void Retail_UnfixedInfoAlone_DecryptsButCantEncrypt()
    {
        var raw = RetailKeys.Required.Raw;
        var decryptOnly = AmiiboKeySet.FromUnfixedInfo(raw.AsSpan(0, 80));

        Assert.False(decryptOnly.CanEncrypt);
        Assert.Throws<TagException>(() => decryptOnly.StaticKey);
        Assert.Throws<TagException>(() => decryptOnly.Raw);

        var (_, v2) = RetailKeyFixtures.V2(RetailKeyFixtures.UidA, "salt A", "Retail A");
        Assert.Equal("Retail A", v2.GetNickname(decryptOnly));
        var v3 = RetailKeyFixtures.V3(RetailKeyFixtures.UidA, "salt A", "Retail A");
        Assert.Equal("Retail A", v3.GetNickname(decryptOnly));

        var plain = v3.Decrypt(decryptOnly);
        Assert.Throws<TagException>(() => AmiiboCrypto.Encrypt(plain, decryptOnly));
        Assert.Throws<TagException>(() => v2.Patch(v2.Uid, decryptOnly));
    }
}
