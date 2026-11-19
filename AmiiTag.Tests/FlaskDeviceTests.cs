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
}
