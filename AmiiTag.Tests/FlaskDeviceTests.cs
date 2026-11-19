using AmiiTag.Bluetooth.Devices;
using AmiiTag.Tags;
using Xunit;

namespace AmiiTag.Tests;

public sealed class FlaskDeviceTests
{
    private static TagDump DumpWithTail(uint tail)
    {
        var bytes = new byte[TagDump.DataLength];
        bytes[88] = (byte)(tail >> 24);
        bytes[89] = (byte)(tail >> 16);
        bytes[90] = (byte)(tail >> 8);
        bytes[91] = (byte)tail;
        Assert.True(TagDump.TryCreate(bytes, out var dump));
        return dump!;
    }

    [Fact]
    public void Names_AreDisplayNameTailInBase36AndSet()
    {
        Assert.Equal("Mario|2|0", FlaskDevice.BuildName(DumpWithTail(2), "Mario", []));
        Assert.Equal("Mario|3zfkle|0", FlaskDevice.BuildName(DumpWithTail(0x0E5C0502), "Mario", []));
    }

    [Fact]
    public void Names_AreSanitisedShortenedAndNumbered()
    {
        Assert.Equal("Pokemon Pikachu", FlaskDevice.SanitizeDisplayName("Pokémon_Pikachu!"));
        Assert.Equal("Tag", FlaskDevice.SanitizeDisplayName("\"|<>"));

        var name = FlaskDevice.BuildName(DumpWithTail(0x0E5C0502), "A very long amiibo figure name indeed", []);
        Assert.True(name.Length <= 27);
        Assert.EndsWith("|3zfkle|0", name);
        Assert.Equal(22, name.Length - "|0".Length);

        Assert.Equal("1. Mario|2|0", FlaskDevice.BuildName(DumpWithTail(2), "Mario", ["Mario|2|0"]));
        Assert.Equal("2. Mario|2|0", FlaskDevice.BuildName(DumpWithTail(2), "Mario", ["Mario|2|0", "1. Mario|2|0"]));
    }

    [Fact]
    public void StoredNames_ParseBackToTheAmiiboTail()
    {
        var tag = FlaskDevice.ToStoredTag(3, "Mario|3zfkle|abcd");
        Assert.Equal(3, tag.Index);
        Assert.Equal("Mario", tag.DisplayName);
        Assert.Equal("0e5c0502", tag.AmiiboTail);
        Assert.Equal("abcd", tag.SetKey);

        // A tail shorter than 4 digits (or none) is another NFC tag, as the Flask app shows it.
        Assert.Null(FlaskDevice.ToStoredTag(0, "Mario|2|0").AmiiboTail);
        Assert.Null(FlaskDevice.ToStoredTag(0, "Blank").AmiiboTail);
        Assert.Null(FlaskDevice.ToStoredTag(0, "Mario|3zfkle|0").SetKey);
    }

    [Fact]
    public void NextGenRichList_ParsesTagsFoldersAndMeta()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"tags":[
              {"id":7,"index":0,"name":"Link|3o7vae|1","type":"215","tagId":"01010000034f0902","meta":"3,12,0,1"},
              {"index":1,"name":"Card|0|2","type":"1k","tagId":"0000000000000000","meta":"0,0,0,0"}],
             "folders":[{"id":3,"name":"Zelda"}]}
            """);
        var (tags, folders) = FlaskNextGenDevice.ParseRichList(document.RootElement);

        Assert.Equal(new TagFolder(3, "Zelda"), Assert.Single(folders));
        Assert.Equal("Link", tags[0].DisplayName);
        Assert.Equal(7, tags[0].Id);
        Assert.Equal("034f0902", tags[0].AmiiboTail);
        Assert.Equal(3, tags[0].FolderId);
        Assert.Equal("12", tags[0].Image);
        Assert.True(tags[0].IsNtag215);

        // Without an id the index is used; a MIFARE card has no amiibo tail.
        Assert.Equal(1, tags[1].Id);
        Assert.Null(tags[1].AmiiboTail);
        Assert.False(tags[1].IsNtag215);
        Assert.Equal(0, tags[1].FolderId);
    }

    [Fact]
    public void NextGenRichList_AcceptsAPlainArray()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""[{"index":0,"name":"Mario|3zfkle|0","type":"215","tagId":"000000000e5c0502","meta":"0,0,0,1"}]""");
        var (tags, folders) = FlaskNextGenDevice.ParseRichList(document.RootElement);

        Assert.Empty(folders);
        Assert.Equal("0e5c0502", Assert.Single(tags).AmiiboTail);
    }

    [Theory]
    [InlineData("{\"v\":10,\"d\":0}", true)]
    [InlineData("{\"v\":\"10.1\"}", true)]
    [InlineData("{\"v\":\"8.2\",\"k\":false}", false)]
    [InlineData("{\"v\":\"6.5\",\"d\":\"1\"}", false)]
    [InlineData("\"ERROR\"", false)]
    public void NextGenVersion_IsTenOrLater(string reply, bool nextGen)
    {
        using var document = System.Text.Json.JsonDocument.Parse(reply);
        Assert.Equal(nextGen, FlaskNextGenDevice.IsNextGenVersion(document.RootElement, out _));
    }
}
