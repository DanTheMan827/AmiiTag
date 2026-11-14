using System.Text;
using AmiiTag.Tags;

namespace AmiiTag.Tests;

public class TagDumpTests
{
    [Theory]
    [InlineData(532)]
    [InlineData(540)]
    [InlineData(572)]
    public void AcceptsTheThreeDumpSizes(int length)
    {
        Assert.True(TagDump.TryCreate(new byte[length], out var dump));
        Assert.Equal(length, dump!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(531)]
    [InlineData(573)]
    public void RejectsOtherSizes(int length)
    {
        Assert.False(TagDump.TryCreate(new byte[length], out _));
        Assert.Throws<TagException>(() => new TagDump(new byte[length]));
    }

    [Fact]
    public void RandomUid_HasNxpPrefixAndValidCheckBytes()
    {
        for (var i = 0; i < 50; i++)
        {
            var uid = NtagUid.Random();
            Assert.Equal(0x04, uid[0]);
            Assert.Equal((byte)(0x88 ^ uid[0] ^ uid[1] ^ uid[2]), uid[3]);
            Assert.Equal((byte)(uid[4] ^ uid[5] ^ uid[6] ^ uid[7]), uid[8]);
            Assert.Equal(uid, NtagUid.FromSevenByteUid(NtagUid.ToSevenByteUid(uid)));
        }
    }

    [Fact]
    public void AmiiboPassword_FollowsTheUidFormula()
    {
        var uid = TestData.Uid9(0x10);
        var password = NtagUid.AmiiboPassword(uid);
        Assert.Equal((byte)(0xAA ^ uid[1] ^ uid[4]), password[0]);
        Assert.Equal((byte)(0x55 ^ uid[2] ^ uid[5]), password[1]);
        Assert.Equal((byte)(0xAA ^ uid[4] ^ uid[6]), password[2]);
        Assert.Equal((byte)(0x55 ^ uid[5] ^ uid[7]), password[3]);
    }

    [Fact]
    public void FromAmiiboId_BuildsALockedAmiiboLayout()
    {
        var dump = TestData.Amiibo(TestData.LinkId);

        Assert.True(dump.IsAmiibo);
        Assert.True(dump.IsLocked);
        Assert.Equal(TestData.LinkId, dump.AmiiboIdHex);
        Assert.True(dump.HasAmiiboId);
        Assert.Equal(TestData.Uid9(0x10), dump.Uid);
        Assert.Equal("010", dump.GameSeriesHex);
        Assert.Equal("00", dump.TypeHex);
    }

    [Fact]
    public void Patch_KeepsThePlainTextAndChangesTheCipherText()
    {
        var keys = TestData.Keys;
        var dump = TestData.Amiibo();
        var moved = dump.Patch(TestData.Uid9(0x60), keys);

        Assert.Equal(TestData.Uid9(0x60), moved.Uid);
        Assert.Equal(dump.Decrypt(keys), moved.Decrypt(keys));
        Assert.NotEqual(dump.Span[20..52].ToArray(), moved.Span[20..52].ToArray());
        Assert.NotEqual(dump.Span[52..84].ToArray(), moved.Span[52..84].ToArray()); // Tag HMAC.
        Assert.NotEqual(dump.Span[128..160].ToArray(), moved.Span[128..160].ToArray()); // Data HMAC.
        Assert.Equal(dump.Span[84..128].ToArray(), moved.Span[84..128].ToArray()); // ID and salt unchanged.
    }

    [Fact]
    public void Patch_IsDeterministic()
    {
        var keys = TestData.Keys;
        var dump = TestData.Amiibo();
        Assert.Equal(dump.Patch(TestData.Uid9(0x60), keys), dump.Patch(TestData.Uid9(0x60), keys));
    }

    [Fact]
    public void Patch_BackToTheOriginalUid_RestoresTheDump()
    {
        var keys = TestData.Keys;
        var dump = TestData.Amiibo();
        var roundTrip = dump.Patch(TestData.Uid9(0x60), keys).Patch(dump.Uid, keys);
        Assert.Equal(dump.Span[..532].ToArray(), roundTrip.Span[..532].ToArray());
    }

    [Fact]
    public void WithUid_AttachesTheSignature()
    {
        var keys = TestData.Keys;
        var signature = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var dump = TestData.Amiibo().WithUid(TestData.Uid9(0x30), signature, keys);

        Assert.Equal(TagDump.FullLength, dump.Length);
        Assert.Equal(signature, dump.Signature);
        Assert.Equal(new byte[8], dump.Span[532..540].ToArray());
    }

    [Fact]
    public void Nickname_IsReadFromTheEncryptedSettings()
    {
        var keys = TestData.Keys;

        // Build plain text with the settings flag set and a UTF-16BE nickname, then encrypt it via Patch(skipDecrypt).
        var plain = TagDump.FromAmiiboId(TestData.MarioId, keys: null).Data;
        plain[22] = 0x00;
        plain[23] = 0x01;
        Encoding.BigEndianUnicode.GetBytes("Mario!").CopyTo(plain, 32);
        var dump = new TagDump(plain).Patch(TestData.Uid9(0x10), keys, skipDecrypt: true);

        Assert.Equal("Mario!", dump.GetNickname(keys));
        Assert.Equal(string.Empty, dump.GetNickname(null));
    }

    [Fact]
    public void Blank_IsAFactoryNtag215Image()
    {
        var blank = TagDump.Blank();
        Assert.False(blank.IsLocked);
        Assert.False(blank.IsAmiibo);
        Assert.Equal(new byte[] { 0xE1, 0x10, 0x3E, 0x00 }, blank.Span[12..16].ToArray());
        Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0xFF }, blank.Span[524..528].ToArray());
    }

    [Fact]
    public void QrText_RoundTrips()
    {
        var dump = TestData.Amiibo();
        Assert.Equal(dump, TagDump.FromQrText(dump.ToQrText()));
        Assert.Equal(dump, TagDump.FromQrText(" " + dump.ToQrText().Insert(40, "\n") + "\r\n"));
        Assert.Throws<TagException>(() => TagDump.FromQrText(Convert.ToBase64String(new byte[10])));
    }

    [Fact]
    public void KeyFile_MustMatchTheRetailHashUnlessToldOtherwise()
    {
        var raw = TestData.Keys.Raw;
        Assert.False(AmiiboKeySet.IsValidKeyFile(raw));
        Assert.Throws<TagException>(() => AmiiboKeySet.FromKeyFile(raw));
        Assert.Throws<TagException>(() => AmiiboKeySet.FromKeyFile(new byte[100], requireKnownHash: false));
    }

    [Fact]
    public void Originality_RejectsBlankOrBogusSignatures()
    {
        Assert.False(NtagOriginality.Verify(TestData.Uid7(0x10), new byte[32]));
        Assert.False(NtagOriginality.Verify(TestData.Uid7(0x10), Enumerable.Repeat((byte)0x11, 32).ToArray()));
        Assert.False(NtagOriginality.Verify(new byte[5], new byte[32]));
    }
}
