using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// Checks the NTAG I²C Plus 2K simulator against the NT3H2111/2211 datasheet (as <see cref="Ntag215SimulatorTests"/>
/// does for NTAG215), including every command a v3 amiibo reader or writer uses, directly and through the MFRC522 bridge.
/// </summary>
public class NtagI2cPlusSimulatorTests
{
    private static readonly byte[] Password = [0x11, 0x22, 0x33, 0x44];

    private static TagResponse Send(TagSimulator tag, params byte[] command) => tag.Transceive(command);

    private static void AssertAck(TagResponse response) => Assert.Equal(TagResponseKind.Ack, response.Kind);

    private static void AssertNak(TagResponse response, byte value = TagResponse.NakInvalidArgument)
    {
        Assert.Equal(TagResponseKind.Nak, response.Kind);
        Assert.Equal(value, response.Nibble);
    }

    private static void SelectSector(TagSimulator tag, byte sector)
    {
        AssertAck(Send(tag, 0xC2, 0xFF));
        Assert.Equal(TagResponseKind.None, Send(tag, sector, 0x00, 0x00, 0x00).Kind);
    }

    /// <summary>
    /// A blank tag with PWD 11 22 33 44, PACK AA BB, the given AUTH0/ACCESS, reactivated so they're latched.
    /// </summary>
    private static NtagI2cPlusSimulator Protected(byte auth0, byte access = 0x00)
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, [0xA2, 0xE5, .. Password]));
        AssertAck(Send(tag, 0xA2, 0xE6, 0xAA, 0xBB, 0x00, 0x00));
        AssertAck(Send(tag, 0xA2, 0xE4, access, 0x00, 0x00, 0x00));
        AssertAck(Send(tag, 0xA2, 0xE3, 0x00, 0x00, 0x00, auth0));
        tag.Activate();
        return tag;
    }

    // ---------------------------------------------------------------- Identification

    [Fact]
    public void GetVersion_ReportsNtagI2cPlus2k()
    {
        var response = Send(NtagI2cPlusSimulator.CreateBlank(), 0x60);
        Assert.Equal(new byte[] { 0x00, 0x04, 0x04, 0x05, 0x02, 0x02, 0x15, 0x03 }, response.Data);
        Assert.True(new NtagVersion(response.Data).IsNtagI2cPlus2k);
    }

    [Fact]
    public void ReadSig_Returns32Bytes_AndNaksOtherAddresses()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        tag.Signature = Enumerable.Range(0, 32).Select(i => (byte)(0xC0 + i)).ToArray();
        Assert.Equal(tag.Signature, Send(tag, 0x3C, 0x00).Data);
        AssertNak(Send(tag, 0x3C, 0x01));
    }

    [Fact]
    public void Uid_IsSevenBytesWithoutCheckBytes()
    {
        byte[] uid = [0x04, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];
        var tag = NtagI2cPlusSimulator.CreateBlank(uid);
        var pages = Send(tag, 0x30, 0x00).Data;
        Assert.Equal(uid, pages[..7]);
        Assert.Equal(uid, tag.Uid7);
    }

    // ---------------------------------------------------------------- READ / FAST_READ

    [Fact]
    public void Read_ReturnsFourPages()
    {
        var figure = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys);
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        Assert.Equal(figure.Span[0x10..0x20].ToArray(), Send(tag, 0x30, 0x04).Data);
    }

    [Fact]
    public void Read_RollsOverInsideTheSector()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        SelectSector(tag, 1);
        AssertAck(Send(tag, 0xA2, 0xFF, 1, 2, 3, 4));
        AssertAck(Send(tag, 0xA2, 0x00, 5, 6, 7, 8));

        var data = Send(tag, 0x30, 0xFF).Data;
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, data[..8]);
    }

    [Theory]
    [InlineData(0xE8)] // E8, E9, EA (invalid)
    [InlineData(0xEA)]
    [InlineData(0xEC)] // EC, ED, EE (invalid)
    [InlineData(0xEE)]
    public void Read_TouchingAnInvalidPage_NaksAndHaltsTheTag(int page)
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertNak(Send(tag, 0x30, (byte)page));
        Assert.True(tag.IsHalted);
        Assert.Equal(TagResponseKind.None, Send(tag, 0x30, 0x00).Kind);

        tag.Activate();
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x30, 0x00).Kind);
    }

    [Fact]
    public void Read_E6_ReachesTheConfigurationWithoutInvalidPages()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        var data = Send(tag, 0x30, 0xE6).Data;
        Assert.Equal(tag.GetPage(0, 0xE8), data[8..12]);
        Assert.Equal(tag.GetPage(0, 0xE9), data[12..16]);
    }

    [Fact]
    public void FastRead_ReturnsTheRequestedRange()
    {
        var figure = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys);
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        Assert.Equal(figure.Span[..(0xE5 * 4)].ToArray(), Send(tag, 0x3A, 0x00, 0xE4).Data);
    }

    [Theory]
    [InlineData(0x05, 0x04)] // End before start.
    [InlineData(0xE0, 0xEF)] // Crosses invalid pages.
    [InlineData(0xF0, 0xFF)] // SRAM while pass-through is off.
    public void FastRead_InvalidRange_Naks(int start, int end)
    {
        AssertNak(Send(NtagI2cPlusSimulator.CreateBlank(), 0x3A, (byte)start, (byte)end));
    }

    [Fact]
    public void PwdAndPack_AlwaysReadAsZeros()
    {
        var tag = Protected(auth0: 0xFF);
        Assert.Equal(new byte[8], Send(tag, 0x3A, 0xE5, 0xE6).Data);
        Assert.Equal(Password, tag.GetPage(0, 0xE5));
    }

    // ---------------------------------------------------------------- WRITE / COMPATIBILITY_WRITE

    [Fact]
    public void Write_UserPage_AcksAndPersists()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x40, 0xDE, 0xAD, 0xBE, 0xEF));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Send(tag, 0x30, 0x40).Data[..4]);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0xEA)]
    [InlineData(0xEC)] // Session registers are read only from NFC.
    [InlineData(0xED)]
    [InlineData(0xEE)]
    [InlineData(0xF0)] // SRAM.
    public void Write_ReadOnlyOrInvalidPage_Naks(int page)
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        var before = tag.SectorMemory(0);
        AssertNak(Send(tag, 0xA2, (byte)page, 1, 2, 3, 4));
        Assert.Equal(before, tag.SectorMemory(0));
    }

    [Fact]
    public void CompatibilityWrite_WritesTheFirstFourBytes()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA0, 0x20));
        AssertAck(Send(tag, Enumerable.Range(1, 16).Select(i => (byte)i).ToArray()));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, tag.GetPage(0, 0x20));
    }

    [Fact]
    public void Sectors_HaveIndependentMemory()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x10, 0xAA, 0xAA, 0xAA, 0xAA));
        SelectSector(tag, 1);
        AssertAck(Send(tag, 0xA2, 0x10, 0xBB, 0xBB, 0xBB, 0xBB));

        Assert.Equal(new byte[] { 0xBB, 0xBB, 0xBB, 0xBB }, Send(tag, 0x30, 0x10).Data[..4]);
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, tag.GetPage(0, 0x10));
    }

    // ---------------------------------------------------------------- SECTOR_SELECT

    [Fact]
    public void SectorSelect_FirstPacketAcks_SecondIsAPassiveAck()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xC2, 0xFF));
        Assert.Equal(TagResponseKind.None, Send(tag, 0x01, 0x00, 0x00, 0x00).Kind);
        Assert.False(tag.IsHalted);
        Assert.Equal(1, tag.CurrentSector);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(0xFF)]
    public void SectorSelect_MissingSector_Naks(int sector)
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xC2, 0xFF));
        AssertNak(Send(tag, (byte)sector, 0x00, 0x00, 0x00));
        Assert.True(tag.IsHalted);
    }

    [Fact]
    public void SectorSelect_InterruptedByAnotherCommand_Naks()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xC2, 0xFF));
        AssertNak(Send(tag, 0x30, 0x00));
        Assert.Equal(0, tag.CurrentSector);
    }

    [Fact]
    public void Activation_ReturnsToSectorZero()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        SelectSector(tag, 1);
        tag.Activate();
        Assert.Equal(0, tag.CurrentSector);
    }

    [Fact]
    public void NfcDisSec1_MakesSectorOneUnreachable()
    {
        var tag = Protected(auth0: 0xFF, access: NtagI2cPlusSimulator.AccessNfcDisSec1);
        AssertAck(Send(tag, 0xC2, 0xFF));
        AssertNak(Send(tag, 0x01, 0x00, 0x00, 0x00));
    }

    // ---------------------------------------------------------------- Locking

    [Fact]
    public void Page2_OnlyLockBytesChange_AndTheyAreOneTimeProgrammable()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x02, 0x99, 0x99, 0x10, 0x01));
        AssertAck(Send(tag, 0xA2, 0x02, 0x00, 0x00, 0x00, 0x00));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x10, 0x01 }, tag.GetPage(0, 0x02));

        Assert.True(tag.IsPageLocked(0, 4));
        Assert.True(tag.IsPageLocked(0, 8));
        AssertNak(Send(tag, 0xA2, 0x04, 1, 2, 3, 4));
    }

    [Fact]
    public void CapabilityContainer_BitsCanOnlyBeSet()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x03, 0xE1, 0x10, 0x00, 0x00));
        AssertAck(Send(tag, 0xA2, 0x03, 0x00, 0x00, 0xEA, 0x0F));
        Assert.Equal(new byte[] { 0xE1, 0x10, 0xEA, 0x0F }, tag.GetPage(0, 0x03));
    }

    [Fact]
    public void BlockLockBits_FreezeTheirLockBits()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x02, 0x00, 0x00, 0x02, 0x00)); // BL9-4
        AssertAck(Send(tag, 0xA2, 0x02, 0x00, 0x00, 0x10, 0x01)); // L4 and L8 are frozen.
        Assert.False(tag.IsPageLocked(0, 4));
        Assert.False(tag.IsPageLocked(0, 8));
    }

    [Fact]
    public void DynamicLock_LocksBlocksInBothSectors_AndIsOneTimeProgrammable()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0xE2, 0x02, 0x01, 0x01, 0xFF));
        AssertAck(Send(tag, 0xA2, 0xE2, 0x00, 0x00, 0x00, 0x00));
        Assert.Equal(new byte[] { 0x02, 0x01, 0x01, 0x00 }, tag.GetPage(0, 0xE2)); // Byte 3 is RFU.

        Assert.False(tag.IsPageLocked(0, 0x1F));
        Assert.True(tag.IsPageLocked(0, 0x20));
        Assert.True(tag.IsPageLocked(0, 0x2F));
        Assert.False(tag.IsPageLocked(0, 0x30));
        Assert.True(tag.IsPageLocked(0, 0x95));
        Assert.True(tag.IsPageLocked(1, 0x1F));
        Assert.False(tag.IsPageLocked(1, 0x20));

        AssertNak(Send(tag, 0xA2, 0x20, 1, 2, 3, 4));
        tag.Activate();
        AssertAck(Send(tag, 0xA2, 0x30, 1, 2, 3, 4));
        SelectSector(tag, 1);
        AssertNak(Send(tag, 0xA2, 0x10, 1, 2, 3, 4));
        tag.Activate();
        SelectSector(tag, 1);
        AssertAck(Send(tag, 0xA2, 0x20, 1, 2, 3, 4));
    }

    [Fact]
    public void AmiiboTemplateLocks_CoverIdSaltAndSectorOne()
    {
        var tag = NtagI2cPlusSimulator.FromImage(AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys));
        Assert.True(tag.IsPageLocked(0, 0x03));  // CC
        Assert.True(tag.IsPageLocked(0, 0x0F));  // Static L15
        Assert.True(tag.IsPageLocked(0, 0x15));  // ID
        Assert.True(tag.IsPageLocked(0, 0x1F));  // Salt
        Assert.False(tag.IsPageLocked(0, 0x04)); // Header
        Assert.False(tag.IsPageLocked(0, 0x30)); // Data HMAC / body
        Assert.All(Enumerable.Range(0, 256), page => Assert.True(tag.IsPageLocked(1, page)));
    }

    // ---------------------------------------------------------------- Password protection

    [Fact]
    public void Auth0_ProtectsWritesInBothSectorsUntilPwdAuth()
    {
        var tag = Protected(auth0: 0x10);
        AssertAck(Send(tag, 0xA2, 0x0F, 1, 2, 3, 4)); // Below AUTH0.
        AssertNak(Send(tag, 0xA2, 0x10, 1, 2, 3, 4));

        tag.Activate();
        SelectSector(tag, 1);
        AssertNak(Send(tag, 0xA2, 0x00, 1, 2, 3, 4));

        tag.Activate();
        Assert.Equal(new byte[] { 0xAA, 0xBB }, Send(tag, [0x1B, .. Password]).Data);
        AssertAck(Send(tag, 0xA2, 0x10, 1, 2, 3, 4));
        SelectSector(tag, 1);
        AssertAck(Send(tag, 0xA2, 0x00, 1, 2, 3, 4));
    }

    [Fact]
    public void Auth0_IsLatchedAtActivation()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0xE3, 0x00, 0x00, 0x00, 0x10));
        AssertAck(Send(tag, 0xA2, 0x20, 1, 2, 3, 4)); // Still unprotected in this session.

        tag.Activate();
        AssertNak(Send(tag, 0xA2, 0x20, 1, 2, 3, 4));
    }

    [Fact]
    public void Auth0AboveEBh_DisablesProtection()
    {
        var tag = Protected(auth0: 0xEC);
        AssertAck(Send(tag, 0xA2, 0xE1, 1, 2, 3, 4));
        SelectSector(tag, 1);
        AssertAck(Send(tag, 0xA2, 0x80, 1, 2, 3, 4));
    }

    [Fact]
    public void NfcProt_ProtectsReadsToo()
    {
        var tag = Protected(auth0: 0x10, access: NtagI2cPlusSimulator.AccessNfcProt);
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x30, 0x0C).Kind); // 0C-0F
        AssertNak(Send(tag, 0x30, 0x0D)); // 0D-10

        tag.Activate();
        Send(tag, [0x1B, .. Password]);
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x30, 0x10).Kind);
    }

    [Fact]
    public void PwdAuth_WrongPassword_NaksAndCountsAttempts()
    {
        var tag = Protected(auth0: 0x10);
        AssertNak(Send(tag, 0x1B, 0, 0, 0, 0));
        Assert.Equal(1, tag.FailedAuthAttempts);
        Assert.False(tag.IsAuthenticated);

        tag.Activate();
        Send(tag, [0x1B, .. Password]);
        Assert.Equal(0, tag.FailedAuthAttempts);
    }

    [Fact]
    public void AuthLim_PermanentlyBlocksAuthenticationOnceReached()
    {
        var tag = Protected(auth0: 0x10, access: 0x01); // 2^1 attempts.
        AssertNak(Send(tag, 0x1B, 0, 0, 0, 0));
        tag.Activate();
        AssertNak(Send(tag, 0x1B, 0, 0, 0, 0), TagResponse.NakAuthCounterOverflow);
        tag.Activate();
        AssertNak(Send(tag, [0x1B, .. Password]), TagResponse.NakAuthCounterOverflow);
    }

    // ---------------------------------------------------------------- Configuration and session registers

    [Fact]
    public void ConfigurationRegisters_AreWritableUntilRegLockNfc()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0xE8, 0x01, 0x00, 0xF8, 0x48));
        AssertAck(Send(tag, 0xA2, 0xE9, 0x08, 0x01, NtagI2cPlusSimulator.RegLockNfc, 0x00));
        Assert.True(tag.IsPageLocked(0, 0xE8));
        AssertNak(Send(tag, 0xA2, 0xE8, 0x00, 0x00, 0xF8, 0x48));
        Assert.Equal(new byte[] { 0x01, 0x00, 0xF8, 0x48 }, tag.GetPage(0, 0xE8));
    }

    [Fact]
    public void SessionRegisters_ReportPassThroughAndSramReady()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        tag.RequireLockOnRequest = false; // Explicit legacy replay profile.
        var session = Send(tag, 0x3A, 0xEC, 0xED).Data;
        Assert.Equal(0, session[0] & NtagI2cPlusSimulator.NcPassThroughOn);
        Assert.Equal(0, session[6] & NtagI2cPlusSimulator.NsSramRfReady);

        tag.SramMirrored = true;
        session = Send(tag, 0x3A, 0xEC, 0xED).Data;
        Assert.NotEqual(0, session[0] & NtagI2cPlusSimulator.NcPassThroughOn);
        Assert.NotEqual(0, session[6] & NtagI2cPlusSimulator.NsSramRfReady);
    }

    // ---------------------------------------------------------------- SRAM (lock-on)

    [Fact]
    public void Sram_IsReadableOnlyOnceTheI2cSideHasFilledIt()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        tag.RequireLockOnRequest = false; // Explicit legacy replay profile.
        tag.Sram = Enumerable.Range(0, 64).Select(i => (byte)(i * 5)).ToArray();
        tag.SramMirrored = true;
        tag.SramReadyAfterSessionReads = 1;

        AssertNak(Send(tag, 0x3A, 0xF0, 0xF0));
        tag.Activate();
        Assert.Equal(0, Send(tag, 0x3A, 0xEC, 0xED).Data[6] & NtagI2cPlusSimulator.NsSramRfReady);
        Assert.Equal(tag.Sram, Send(tag, 0x3A, 0xF0, 0xFF).Data);
    }

    [Fact]
    public void Sram_ReadingTheLastPageHandsItBackToI2c()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        tag.RequireLockOnRequest = false; // Explicit legacy replay profile.
        tag.Sram = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        tag.SramMirrored = true;

        Assert.Equal(TagResponseKind.Data, Send(tag, 0x3A, 0xF0, 0xF7).Kind);
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x3A, 0xF8, 0xFF).Kind);
        AssertNak(Send(tag, 0x3A, 0xF0, 0xF0));

        tag.Activate();
        Assert.Equal(0, Send(tag, 0x3A, 0xEC, 0xED).Data[6] & NtagI2cPlusSimulator.NsSramRfReady);
        Assert.NotEqual(0, Send(tag, 0x3A, 0xEC, 0xED).Data[6] & NtagI2cPlusSimulator.NsSramRfReady);
        Assert.Equal(tag.Sram, Send(tag, 0x3A, 0xF0, 0xFF).Data);
    }

    [Fact]
    public void Sram_FromABinIsServed()
    {
        var figure = AmiiboV3Dump.Create(Hex.FromHex(AmiiboV3Tests.V3Id), TestData.Keys, lockOn: new byte[] { 1, 2, 3 });
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        tag.RequireLockOnRequest = false; // Explicit legacy replay profile.
        Assert.Equal(figure.LockOn, Send(tag, 0x3A, 0xF0, 0xFF).Data);
        Assert.Equal(new byte[4], tag.GetPage(0, 0xF0)); // SRAM isn't EEPROM.
    }

    // ---------------------------------------------------------------- Frame level

    [Fact]
    public void Halt_SilencesTheTagUntilReactivated()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        Assert.Equal(TagResponseKind.None, Send(tag, 0x50, 0x00).Kind);
        Assert.True(tag.IsHalted);
        Assert.Equal(TagResponseKind.None, Send(tag, 0x60).Kind);
        tag.Activate();
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x60).Kind);
    }

    [Fact]
    public void UnknownCommand_Naks()
    {
        AssertNak(Send(NtagI2cPlusSimulator.CreateBlank(), 0x39, 0x02)); // READ_CNT isn't an NTAG I²C command.
    }

    [Fact]
    public void Frame_WithBadCrc_IsRejectedWithCrcNak()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        var response = tag.TransceiveFrame([0x30, 0x00, 0x00, 0x00]);
        Assert.Equal(TagResponseKind.Nak, response.Kind);
        Assert.Equal(TagResponse.NakCrcError, response.Nibble);
    }

    [Fact]
    public void Frame_DataResponsesCarryCrc()
    {
        var tag = NtagI2cPlusSimulator.CreateBlank();
        var response = tag.TransceiveFrame(Crc14443A.Append([0x60]));
        Assert.True(Crc14443A.Check(response.Data));
        Assert.Equal(AmiiboV3Dump.Version, response.Data[..^2]);
    }

    // ---------------------------------------------------------------- Every v3 amiibo command, through the MFRC522

    [Fact]
    public async Task EveryV3AmiiboCommand_WorksThroughTheMfrc522Bridge()
    {
        var raw = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys).Data;
        Password.CopyTo(raw, 0xE5 * 4);
        raw[0xE6 * 4] = 0xAA;
        raw[0xE6 * 4 + 1] = 0xBB;
        var tag = new NtagI2cPlusSimulator(raw) { Signature = Enumerable.Repeat((byte)0x5A, 32).ToArray() };

        var bridge = new SimulatedMfrc522Bridge(tag);
        var provider = new Mfrc522NfcProvider(new SimulatedSerialPortFactory(bridge));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await provider.ConnectAsync(new NfcConnectOptions(), cts.Token);
        var client = new Ntag215Client(connection);

        Assert.True((await client.GetVersionAsync(cts.Token))!.IsNtagI2cPlus2k);
        Assert.Equal(tag.Signature, await client.ReadSignatureAsync(cts.Token));
        Assert.Equal(raw[..16], await connection.TransceiveAsync([0x30, 0x00], cts.Token));
        Assert.Equal(raw[..(15 * 4)], await connection.TransceiveAsync([0x3A, 0x00, 0x0E], cts.Token));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, await client.PasswordAuthAsync(Password, cts.Token));

        await client.WritePageAsync(0x40, new byte[] { 9, 8, 7, 6 }, cts.Token);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, tag.GetPage(0, 0x40));

        await AmiiboV3NfcOperations.SectorSelectAsync(connection, 1, cts.Token);
        Assert.Equal(1, tag.CurrentSector);
        Assert.Equal(16, (await connection.TransceiveAsync([0x30, 0x00], cts.Token)).Length);
        await AmiiboV3NfcOperations.SectorSelectAsync(connection, 0, cts.Token);
        Assert.Equal(0, tag.CurrentSector);
    }
}
