using AmiiTag.Nfc;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// Readers are used in direct command mode (raw commands to the tag). Without it, tags can still be read but not written.
/// </summary>
public class DirectCommandModeTests
{
    [Fact]
    public async Task Write_IsRefusedWithoutDirectCommandMode_BeforeTouchingTheTag()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var before = tag.Memory;
        await using var connection = new SimulatedTagConnection(tag) { IsDirectCommandMode = false };

        var error = await Assert.ThrowsAsync<NfcException>(() =>
            AmiiboNfcOperations.WriteTagAsync(connection, TestData.Amiibo(), TestData.Keys));

        Assert.Contains("direct command mode", error.Message);
        Assert.Empty(tag.CommandLog);
        Assert.Equal(before, tag.Memory);
    }

    [Fact]
    public async Task Ntag215_CanStillBeReadWithoutDirectCommandMode()
    {
        var dump = TestData.Amiibo(); // Each call has a new random salt, so compare with this one.
        var tag = Ntag215Simulator.FromAmiiboDump(dump);
        await using var connection = new SimulatedTagConnection(tag) { IsDirectCommandMode = false };

        var read = Assert.IsType<TagDump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection));

        Assert.Equal(dump.Span[..TagDump.ShortLength].ToArray(), read.Span[..TagDump.ShortLength].ToArray());
        Assert.All(tag.CommandLog, c => Assert.Equal(0x30, c[0]));
    }

    [Fact]
    public async Task V3_CanStillBeReadWithoutDirectCommandMode()
    {
        var figure = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys);
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        await using var connection = new SimulatedTagConnection(tag) { IsDirectCommandMode = false };

        var read = Assert.IsType<AmiiboV3Dump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection));

        // Sector 0 (all of the amiibo data) is read; sector 1 needs SECTOR_SELECT.
        Assert.Equal(figure.Span[..(0xEA * 4)].ToArray(), read.Span[..(0xEA * 4)].ToArray());
        Assert.True(read.Authenticate(TestData.Keys));
        Assert.Equal(AmiiboV3Tests.V3Id, read.AmiiboIdHex);
        Assert.All(tag.CommandLog, c => Assert.Equal(0x30, c[0]));
    }

    [Fact]
    public async Task DirectCommandMode_IsTheDefault()
    {
        await using var connection = new SimulatedTagConnection(Ntag215Simulator.CreateBlank());
        Assert.True(((INfcTagConnection)connection).IsDirectCommandMode);
        Assert.Equal(NfcWriteKind.FullWrite, await AmiiboNfcOperations.WriteTagAsync(connection, TestData.Amiibo(), TestData.Keys));
    }
}
