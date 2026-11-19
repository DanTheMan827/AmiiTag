using AmiiTag.Tags;

namespace AmiiTag.Tests;

/// <summary>
/// Amiibo validation: blank and foreign tags must never be described as amiibo (a blank tag's zero bytes would
/// otherwise read as amiibo series 00, Super Smash Bros.), and signatures are checked when the keys are loaded.
/// </summary>
public class AmiiboValidatorTests
{
    [Fact]
    public void FactoryBlankNtag215_IsBlank()
    {
        var result = AmiiboValidator.Validate(TagDump.Blank(), TestData.Keys);
        Assert.Equal(AmiiboValidity.Blank, result.Status);
        Assert.False(result.IsAmiibo);
    }

    [Fact]
    public void BlankWithAnEmptyNdefMessage_IsBlank()
    {
        var raw = TagDump.Blank().Data;
        new byte[] { 0x03, 0x00, 0xFE }.CopyTo(raw, 0x10);
        Assert.Equal(AmiiboValidity.Blank, AmiiboValidator.Validate(new TagDump(raw), TestData.Keys).Status);
    }

    [Fact]
    public void OtherData_IsNotAnAmiibo()
    {
        var raw = TagDump.Blank().Data;
        new byte[] { 0x03, 0x0B, 0xD1, 0x01, 0x07, 0x54, 0x02, 0x65, 0x6E, 0x48, 0x69, 0x21, 0xFE }.CopyTo(raw, 0x10); // NDEF text record.
        var result = AmiiboValidator.Validate(new TagDump(raw), TestData.Keys);
        Assert.Equal(AmiiboValidity.NotAmiibo, result.Status);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Amiibo_WithTheKeys_IsValid()
    {
        var result = AmiiboValidator.Validate(TestData.Amiibo(), TestData.Keys);
        Assert.Equal(AmiiboValidity.Valid, result.Status);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Amiibo_WithoutTheKeys_IsUnverified()
    {
        var result = AmiiboValidator.Validate(TestData.Amiibo(), keys: null);
        Assert.Equal(AmiiboValidity.Unverified, result.Status);
        Assert.True(result.IsAmiibo);
    }

    [Fact]
    public void Amiibo_WithChangedData_IsCorrupt()
    {
        var raw = TestData.Amiibo().Data;
        raw[0x100] ^= 0x01;
        Assert.Equal(AmiiboValidity.Corrupt, AmiiboValidator.Validate(new TagDump(raw), TestData.Keys).Status);
    }

    [Fact]
    public void WrongUidCheckBytes_IsNotAnAmiibo()
    {
        var raw = TestData.Amiibo().Data;
        raw[3] ^= 0xFF;
        Assert.Equal(AmiiboValidity.NotAmiibo, AmiiboValidator.Validate(new TagDump(raw), TestData.Keys).Status);
    }

    [Fact]
    public void MissingHeaderMagic_IsNotAnAmiibo()
    {
        var raw = TestData.Amiibo().Data;
        raw[0x10] = 0x00;
        Assert.Equal(AmiiboValidity.NotAmiibo, AmiiboValidator.Validate(new TagDump(raw), TestData.Keys).Status);
    }

    [Fact]
    public void UnlockedAmiiboDump_IsStillValid_WithANote()
    {
        var raw = TestData.Amiibo().Data;
        raw[520] = 0x00; // Dynamic lock bytes never written (e.g. an emulator's dump).
        var result = AmiiboValidator.Validate(new TagDump(raw), TestData.Keys);
        Assert.Equal(AmiiboValidity.Valid, result.Status);
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public void DecryptOnlyKeys_CantVerify()
    {
        var keys = AmiiboKeySet.FromKeyFile(TestData.Keys.Raw, requireKnownHash: false);
        Assert.Equal(AmiiboValidity.Valid, AmiiboValidator.Validate(TestData.Amiibo(), keys).Status);
    }

    [Fact]
    public void V3_Generated_IsValid_AndBlankV3IsBlank()
    {
        var v3 = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys);
        Assert.Equal(AmiiboValidity.Valid, AmiiboValidator.Validate(v3, TestData.Keys).Status);
        Assert.Equal(AmiiboValidity.Blank, AmiiboValidator.Validate(new AmiiboV3Dump(new byte[AmiiboV3Dump.ImageLength]), TestData.Keys).Status);

        var raw = v3.Data;
        raw[0x0E0 + 4] ^= 0x01;
        Assert.Equal(AmiiboValidity.Corrupt, AmiiboValidator.Validate(new AmiiboV3Dump(raw), TestData.Keys).Status);
    }
}
