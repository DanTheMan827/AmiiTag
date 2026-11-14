using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// Checks the simulator itself against the NTAG213/215/216 datasheet, so the higher level tests are built on solid ground.
/// </summary>
public class Ntag215SimulatorTests
{
    private static TagResponse Send(TagSimulator tag, params byte[] command) => tag.Transceive(command);

    private static void AssertAck(TagResponse response) => Assert.Equal(TagResponseKind.Ack, response.Kind);

    private static void AssertNak(TagResponse response, byte value = TagResponse.NakInvalidArgument)
    {
        Assert.Equal(TagResponseKind.Nak, response.Kind);
        Assert.Equal(value, response.Nibble);
    }

    [Fact]
    public void Crc_A_MatchesTheIso14443HaltCommand()
    {
        // HLTA is 50 00 with CRC_A 57 CD.
        Assert.Equal(new byte[] { 0x57, 0xCD }, Crc14443A.Compute([0x50, 0x00]));
        Assert.True(Crc14443A.Check([0x50, 0x00, 0x57, 0xCD]));
        Assert.False(Crc14443A.Check([0x50, 0x00, 0x57, 0xCE]));
    }

    [Fact]
    public void GetVersion_ReportsNtag215()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var response = Send(tag, 0x60);
        Assert.Equal(Ntag215Simulator.VersionNtag215, response.Data);
        Assert.Equal(0x11, response.Data[6]);
    }

    [Fact]
    public void Blank_HasFactoryMemoryLayout()
    {
        var tag = Ntag215Simulator.CreateBlank(TestData.Uid7(0x20));
        var uid9 = TestData.Uid9(0x20);

        Assert.Equal(uid9, tag.Memory.AsSpan(0, 9).ToArray());
        Assert.Equal((byte)(0x88 ^ uid9[0] ^ uid9[1] ^ uid9[2]), tag.Memory[3]);
        Assert.Equal((byte)(uid9[4] ^ uid9[5] ^ uid9[6] ^ uid9[7]), tag.Memory[8]);
        Assert.Equal(new byte[] { 0xE1, 0x10, 0x3E, 0x00 }, tag.GetPage(3));
        Assert.Equal(new byte[] { 0x04, 0x00, 0x00, 0xFF }, tag.GetPage(Ntag215Simulator.Cfg0Page));
        Assert.Equal(new byte[] { 0x00, 0x05, 0x00, 0x00 }, tag.GetPage(Ntag215Simulator.Cfg1Page));
    }

    [Fact]
    public void Read_ReturnsFourPages()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var response = Send(tag, 0x30, 0x00);
        Assert.Equal(tag.Memory.AsSpan(0, 16).ToArray(), response.Data);
    }

    [Fact]
    public void Read_RollsOverToPageZero()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var response = Send(tag, 0x30, Ntag215Simulator.Cfg1Page);

        // Pages 132, 133 (PWD, reads 0), 134 (PACK, reads 0), then page 0.
        Assert.Equal(tag.GetPage(Ntag215Simulator.Cfg1Page), response.Data.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[8], response.Data.AsSpan(4, 8).ToArray());
        Assert.Equal(tag.GetPage(0), response.Data.AsSpan(12, 4).ToArray());
    }

    [Fact]
    public void Read_InvalidAddress_NaksAndHaltsTheTag()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertNak(Send(tag, 0x30, 0x87));
        Assert.True(tag.IsHalted);
        Assert.Equal(TagResponseKind.None, Send(tag, 0x30, 0x00).Kind);

        tag.Activate();
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x30, 0x00).Kind);
    }

    [Fact]
    public void FastRead_ReturnsTheRequestedRange()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var response = Send(tag, 0x3A, 0x00, 0x86);
        Assert.Equal(135 * 4, response.Data.Length);
        Assert.Equal(tag.Memory.AsSpan(0, 133 * 4).ToArray(), response.Data.AsSpan(0, 133 * 4).ToArray());
    }

    [Theory]
    [InlineData(0x05, 0x04)] // End before start.
    [InlineData(0x00, 0x87)] // Past the last page.
    public void FastRead_InvalidRange_Naks(int start, int end)
    {
        AssertNak(Send(Ntag215Simulator.CreateBlank(), 0x3A, (byte)start, (byte)end));
    }

    [Fact]
    public void Write_UserPage_AcksAndPersists()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x10, 0xDE, 0xAD, 0xBE, 0xEF));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, tag.GetPage(0x10));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, Send(tag, 0x30, 0x10).Data.AsSpan(0, 4).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x87)]
    public void Write_ReadOnlyOrInvalidPage_Naks(int page)
    {
        var tag = Ntag215Simulator.CreateBlank();
        var before = tag.Memory;
        AssertNak(Send(tag, 0xA2, (byte)page, 1, 2, 3, 4));
        Assert.Equal(before, tag.Memory);
    }

    [Fact]
    public void CompatibilityWrite_WritesTheFirstFourBytes()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA0, 0x20));
        AssertAck(tag.Transceive([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, tag.GetPage(0x20));
    }

    [Fact]
    public void Page2_OnlyLockBytesChange_AndTheyAreOneTimeProgrammable()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var serial = tag.GetPage(2).AsSpan(0, 2).ToArray();

        AssertAck(Send(tag, 0xA2, 0x02, 0x00, 0x00, 0x10, 0x01));
        AssertAck(Send(tag, 0xA2, 0x02, 0xFF, 0xFF, 0x00, 0x00)); // Clearing is ignored.

        var page = tag.GetPage(2);
        Assert.Equal(serial, page.AsSpan(0, 2).ToArray());
        Assert.Equal(0x10, page[2]);
        Assert.Equal(0x01, page[3]);
    }

    [Fact]
    public void CapabilityContainer_BitsCanOnlyBeSet()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x03, 0xF1, 0x10, 0xFF, 0xEE));
        Assert.Equal(new byte[] { 0xF1, 0x10, 0xFF, 0xEE }, tag.GetPage(3));

        AssertAck(Send(tag, 0xA2, 0x03, 0x00, 0x00, 0x00, 0x00));
        Assert.Equal(new byte[] { 0xF1, 0x10, 0xFF, 0xEE }, tag.GetPage(3));
    }

    [Theory]
    [InlineData(0x10, 0x00, 4)]  // L4
    [InlineData(0x80, 0x00, 7)]  // L7
    [InlineData(0x00, 0x01, 8)]  // L8
    [InlineData(0x00, 0x80, 15)] // L15
    [InlineData(0x08, 0x00, 3)]  // L-CC
    public void StaticLockBit_MakesPageReadOnly(int lock0, int lock1, int page)
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, 0x02, 0, 0, (byte)lock0, (byte)lock1));
        AssertNak(Send(tag, 0xA2, (byte)page, 1, 2, 3, 4));

        tag.Activate();
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x30, (byte)page).Kind); // Still readable.
    }

    [Fact]
    public void BlockLockBits_FreezeTheirLockBits()
    {
        var tag = Ntag215Simulator.CreateBlank();

        // BL9-4 (bit 1) freezes L4-L9, BL15-10 (bit 2) freezes L10-L15.
        AssertAck(Send(tag, 0xA2, 0x02, 0, 0, 0x06, 0x00));
        AssertAck(Send(tag, 0xA2, 0x02, 0, 0, 0xF0, 0xFF));

        Assert.Equal(0x06, tag.GetPage(2)[2]);
        Assert.Equal(0x00, tag.GetPage(2)[3]);
        AssertAck(Send(tag, 0xA2, 0x05, 1, 2, 3, 4)); // Page 5 never got locked.
    }

    [Theory]
    [InlineData(0x01, 16, 31)]
    [InlineData(0x02, 32, 47)]
    [InlineData(0x40, 112, 127)]
    [InlineData(0x80, 128, 129)]
    public void DynamicLockBit_LocksItsSixteenPageBlock(int lockBit, int first, int last)
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, Ntag215Simulator.DynamicLockPage, (byte)lockBit, 0, 0, 0));

        AssertNak(Send(tag, 0xA2, (byte)first, 1, 2, 3, 4));
        tag.Activate();
        AssertNak(Send(tag, 0xA2, (byte)last, 1, 2, 3, 4));
        tag.Activate();
        if (first > 16)
        {
            AssertAck(Send(tag, 0xA2, (byte)(first - 1), 1, 2, 3, 4));
        }
    }

    [Fact]
    public void DynamicLockPage_KeepsRfuByteAndIsOneTimeProgrammable()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, Ntag215Simulator.CmdWrite, Ntag215Simulator.DynamicLockPage, 0x01, 0x00, 0x0F, 0x00));
        AssertAck(Send(tag, Ntag215Simulator.CmdWrite, Ntag215Simulator.DynamicLockPage, 0x00, 0x00, 0x00, 0x00));
        Assert.Equal(new byte[] { 0x01, 0x00, 0x0F, 0xBD }, tag.GetPage(Ntag215Simulator.DynamicLockPage));
    }

    [Fact]
    public void PwdAndPack_AlwaysReadAsZeros()
    {
        var tag = TestData.LockedAmiiboTag();
        Assert.NotEqual(new byte[4], tag.GetPage(Ntag215Simulator.PwdPage));

        var response = Send(tag, 0x3A, Ntag215Simulator.PwdPage, Ntag215Simulator.PackPage);
        Assert.Equal(new byte[8], response.Data);
    }

    [Fact]
    public void Auth0_ProtectsWritesUntilPwdAuth()
    {
        var tag = TestData.LockedAmiiboTag();
        var password = NtagUid.AmiiboPassword(TestData.Uid9(0x10));

        AssertNak(Send(tag, 0xA2, 0x04, 1, 2, 3, 4));
        tag.Activate();

        var pack = Send(tag, [0x1B, .. password]);
        Assert.Equal(new byte[] { 0x80, 0x80 }, pack.Data);
        Assert.True(tag.IsAuthenticated);
        AssertAck(Send(tag, 0xA2, 0x04, 1, 2, 3, 4));

        // Authentication is lost when the tag is halted.
        tag.Halt();
        tag.Activate();
        Assert.False(tag.IsAuthenticated);
    }

    [Fact]
    public void Prot_ProtectsReadsToo()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, Ntag215Simulator.PwdPage, 1, 2, 3, 4));
        AssertAck(Send(tag, 0xA2, Ntag215Simulator.Cfg0Page, 0x04, 0x00, 0x00, 0x10));
        AssertAck(Send(tag, 0xA2, Ntag215Simulator.Cfg1Page, Ntag215Simulator.AccessProt, 0x05, 0x00, 0x00));
        tag.PowerCycle();

        AssertNak(Send(tag, 0x30, 0x10));
        tag.Activate();

        // Reads below AUTH0 roll over at AUTH0.
        var read = Send(tag, 0x30, 0x0E);
        Assert.Equal(tag.GetPage(0x0E), read.Data.AsSpan(0, 4).ToArray());
        Assert.Equal(tag.GetPage(0x00), read.Data.AsSpan(8, 4).ToArray());

        Send(tag, 0x1B, 1, 2, 3, 4);
        Assert.Equal(TagResponseKind.Data, Send(tag, 0x30, 0x10).Kind);
    }

    [Fact]
    public void PwdAuth_WrongPassword_NaksAndCountsAttempts()
    {
        var tag = TestData.LockedAmiiboTag();
        AssertNak(Send(tag, 0x1B, 0, 0, 0, 0));
        Assert.Equal(1, tag.FailedAuthAttempts);
        Assert.False(tag.IsAuthenticated);

        tag.Activate();
        Send(tag, [0x1B, .. NtagUid.AmiiboPassword(TestData.Uid9(0x10))]);
        Assert.Equal(0, tag.FailedAuthAttempts);
    }

    [Fact]
    public void AuthLim_PermanentlyBlocksAuthenticationOnceReached()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, Ntag215Simulator.Cfg1Page, 0x01, 0x05, 0x00, 0x00)); // AUTHLIM = 1 → 2 attempts.
        tag.PowerCycle();

        AssertNak(Send(tag, 0x1B, 0, 0, 0, 0), TagResponse.NakInvalidArgument);
        tag.Activate();
        AssertNak(Send(tag, 0x1B, 0, 0, 0, 0), TagResponse.NakAuthCounterOverflow);
        tag.Activate();

        // Even the right password (factory FF FF FF FF) is refused now.
        AssertNak(Send(tag, 0x1B, 0xFF, 0xFF, 0xFF, 0xFF), TagResponse.NakAuthCounterOverflow);
    }

    [Fact]
    public void CfgLck_LocksTheConfigurationFromTheNextActivation()
    {
        var tag = Ntag215Simulator.CreateBlank();
        AssertAck(Send(tag, 0xA2, Ntag215Simulator.Cfg0Page, 0x00, 0x00, 0x00, 0x04));
        AssertAck(Send(tag, 0xA2, Ntag215Simulator.Cfg1Page, 0x5F, 0x00, 0x00, 0x00));

        // Configuration is latched at activation, so writing CFG1 after AUTH0 in one session works (as amiibo writers rely on).
        tag.PowerCycle();
        Send(tag, 0x1B, 0xFF, 0xFF, 0xFF, 0xFF);
        Assert.True(tag.IsAuthenticated);
        AssertNak(Send(tag, 0xA2, Ntag215Simulator.Cfg1Page, 0x00, 0x00, 0x00, 0x00));
        Assert.Equal(new byte[] { 0x5F, 0x00, 0x00, 0x00 }, tag.GetPage(Ntag215Simulator.Cfg1Page));
    }

    [Fact]
    public void ReadSig_Returns32Bytes()
    {
        var tag = TestData.LockedAmiiboTag();
        var response = Send(tag, 0x3C, 0x00);
        Assert.Equal(32, response.Data.Length);
        Assert.Equal(tag.Signature, response.Data);

        tag.Activate();
        AssertNak(Send(tag, 0x3C, 0x01));
    }

    [Fact]
    public void ReadCnt_CountsTheFirstReadOfEachActivation_AndHonoursPasswordProtection()
    {
        var tag = TestData.LockedAmiiboTag(); // ACCESS 5F: NFC_CNT_EN and NFC_CNT_PWD_PROT set.
        var password = NtagUid.AmiiboPassword(TestData.Uid9(0x10));

        Send(tag, 0x30, 0x00);
        Send(tag, 0x30, 0x04);
        Assert.Equal(1, tag.NfcCounter);

        AssertNak(Send(tag, 0x39, 0x02)); // Needs authentication.
        tag.PowerCycle();
        Send(tag, 0x3A, 0x00, 0x03);
        Send(tag, [0x1B, .. password]);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, Send(tag, 0x39, 0x02).Data);
    }

    [Fact]
    public void ReadCnt_DisabledByDefault()
    {
        AssertNak(Send(Ntag215Simulator.CreateBlank(), 0x39, 0x02));
    }

    [Fact]
    public void Frame_WithBadCrc_IsRejectedWithCrcNak()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var frame = Crc14443A.Append([0x30, 0x00]);
        frame[^1] ^= 0x01;

        var response = tag.TransceiveFrame(frame);
        Assert.Equal(TagResponseKind.Nak, response.Kind);
        Assert.Equal(TagResponse.NakCrcError, response.Nibble);
    }

    [Fact]
    public void Frame_DataResponsesCarryCrc()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var response = tag.TransceiveFrame(Crc14443A.Append([0x60]));
        Assert.Equal(10, response.Data.Length);
        Assert.True(Crc14443A.Check(response.Data));
    }

    [Fact]
    public void UnknownCommand_Naks()
    {
        AssertNak(Send(Ntag215Simulator.CreateBlank(), 0x42));
    }
}
