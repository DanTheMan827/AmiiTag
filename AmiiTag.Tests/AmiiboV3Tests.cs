using System.Text;
using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// amiibo v3 (NTAG I²C Plus 2K): the 2048 byte format, shared crypto, lock-on responses and reading over NFC.
/// </summary>
public class AmiiboV3Tests
{
    public const string V3Id = "0000000000340103";

    private static readonly byte[] V3Uid = [0x04, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76];

    private static AmiiboV3Dump Figure(byte[]? lockOn = null) =>
        AmiiboV3Dump.Create(Hex.FromHex(V3Id), TestData.Keys, V3Uid, lockOn);

    private static byte[] LockOnPayload() => Enumerable.Range(0, LockOnResponse.BodyLength).Select(i => (byte)(i * 3 + 1)).ToArray();

    // ---------------------------------------------------------------- Lock-on

    [Fact]
    public void Crc16Mcrf4xx_MatchesTheStandardCheckValue()
    {
        Assert.Equal(0x6F91, LockOnResponse.Crc16Mcrf4xx(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void LockOn_PayloadIsPaddedAndGetsALittleEndianCrc()
    {
        var response = LockOnResponse.Normalize([0x01, 0x02, 0x03]);
        Assert.Equal(LockOnResponse.Length, response.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, response[..3]);
        Assert.All(response[3..62], b => Assert.Equal(0, b));

        var crc = LockOnResponse.Crc16Mcrf4xx(response.AsSpan(0, 62));
        Assert.Equal((byte)crc, response[62]);
        Assert.Equal((byte)(crc >> 8), response[63]);
        Assert.True(LockOnResponse.HasValidCrc(response));

        response[5] ^= 1;
        Assert.False(LockOnResponse.HasValidCrc(response));
    }

    [Fact]
    public void LockOn_FullResponseIsKeptByteForByte()
    {
        var response = LockOnResponse.Normalize(LockOnPayload());
        Assert.Equal(response, LockOnResponse.Normalize(response));

        var badCrc = (byte[])response.Clone();
        badCrc[63] ^= 0xFF;
        Assert.Equal(badCrc, LockOnResponse.Normalize(badCrc));
    }

    [Fact]
    public void LockOn_InTheSramAreaOfABin_IsDetected()
    {
        var raw = Figure().Data;
        Assert.False(new AmiiboV3Dump(raw).HasLockOn);

        var response = Enumerable.Range(0, 64).Select(i => (byte)(0x80 + i)).ToArray(); // Not even a valid CRC.
        response.CopyTo(raw, 0x3C0);
        var image = Assert.IsType<AmiiboV3Dump>(AmiiboImage.Parse(raw));

        Assert.True(image.HasLockOn);
        Assert.False(image.LockOnCrcValid);
        Assert.Equal(response, image.LockOn);
        Assert.Equal(raw, image.Data); // Saving keeps it in the .bin.
        Assert.Equal(response, ((AmiiboV3Dump)AmiiboImage.FromQrText(image.ToQrText())).LockOn); // So does sharing.
    }

    // ---------------------------------------------------------------- Shared crypto

    [Fact]
    public void Crypto_Ntag215Layout_DecryptsLikeTagDump()
    {
        var dump = TestData.Amiibo();
        var plain = AmiiboCrypto.Decrypt(AmiiboCrypto.ToInternal(dump.Span, AmiiboCrypto.Ntag215Layout), TestData.Keys);
        Assert.Equal(dump.Decrypt(TestData.Keys), plain[AmiiboCrypto.EncryptedStart..(AmiiboCrypto.EncryptedStart + AmiiboCrypto.EncryptedLength)]);
    }

    [Fact]
    public void Crypto_Ntag215Layout_ReEncryptsATagDumpByteForByte()
    {
        var dump = TestData.Amiibo();
        var internalData = AmiiboCrypto.ToInternal(dump.Span, AmiiboCrypto.Ntag215Layout);

        Assert.True(AmiiboCrypto.Authenticate(internalData, TestData.Keys));
        var again = AmiiboCrypto.Encrypt(AmiiboCrypto.Decrypt(internalData, TestData.Keys), TestData.Keys);
        Assert.Equal(internalData, again);
        Assert.Equal(dump.Span[..TagDump.ShortLength].ToArray(), AmiiboCrypto.FromInternal(again, dump.Span, AmiiboCrypto.Ntag215Layout)[..TagDump.ShortLength]);
    }

    [Fact]
    public void Layout_V3IsTheNtag215LayoutWithTheHmacAndBodyMovedBy0x40()
    {
        var v2 = TestData.Amiibo().Data;
        var v3 = new byte[AmiiboV3Dump.ImageLength];
        v2.AsSpan(0, 0x80).CopyTo(v3);
        v2.AsSpan(0x80, 0x188).CopyTo(v3.AsSpan(0xC0)); // Data HMAC + body.

        Assert.Equal(
            AmiiboCrypto.ToInternal(v2, AmiiboCrypto.Ntag215Layout),
            AmiiboCrypto.ToInternal(v3, AmiiboCrypto.NtagI2cPlusLayout));
    }

    // ---------------------------------------------------------------- Format

    [Fact]
    public void Create_UsesAmiiboZerosTemplate()
    {
        var figure = Figure();

        Assert.Equal(AmiiboFormat.NtagI2cPlus2k, figure.Format);
        Assert.Equal(AmiiboV3Dump.ImageLength, figure.Length);
        Assert.Equal(V3Uid, figure.Uid);
        Assert.Equal(V3Id, figure.AmiiboIdHex);
        Assert.True(figure.HasAmiiboId);
        Assert.Equal(new byte[] { 0x44, 0x00, 0x0F, 0xE0 }, figure.GetPage(0, 0x02));
        Assert.Equal(new byte[] { 0xF1, 0x10, 0xFF, 0xEE }, figure.GetPage(0, 0x03));
        Assert.Equal(0xA5, figure.Span[0x10]);
        Assert.Equal(new byte[] { 0x01, 0x00, 0xFF, 0x00 }, figure.GetPage(0, 0xE2));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x04 }, figure.GetPage(0, 0xE3));
        Assert.Equal(new byte[] { 0x07, 0x00, 0x00, 0x00 }, figure.GetPage(0, 0xE4));
        Assert.Equal(new byte[] { 0x41, 0x00, 0xF8, 0x48 }, figure.GetPage(0, 0xEC));
        Assert.Equal(new byte[] { 0x08, 0x01, 0x29, 0x00 }, figure.GetPage(0, 0xED));
        Assert.All(figure.Span[0x80..0xC0].ToArray(), b => Assert.Equal(0, b));
        Assert.False(figure.HasLockOn);
    }

    [Fact]
    public void Create_RandomUidHasTheNxpPrefix()
    {
        var a = AmiiboV3Dump.Create(V3Id, TestData.Keys);
        var b = AmiiboV3Dump.Create(V3Id, TestData.Keys);
        Assert.Equal(0x04, a.Uid[0]);
        Assert.NotEqual(a.Uid, b.Uid);
    }

    [Fact]
    public void Create_IsSignedWithTheKeys()
    {
        var figure = Figure();
        Assert.True(figure.Authenticate(TestData.Keys));

        var tampered = figure.Data;
        tampered[0x0E0 + 10] ^= 0xFF; // Encrypted body.
        Assert.False(new AmiiboV3Dump(tampered).Authenticate(TestData.Keys));

        var moved = figure.Data;
        moved[0x0C0] ^= 0xFF; // Data HMAC in its v3 place.
        Assert.False(new AmiiboV3Dump(moved).Authenticate(TestData.Keys));
    }

    [Fact]
    public void Nickname_IsReadFromV3Data()
    {
        var figure = Figure();
        var plain = figure.Decrypt(TestData.Keys);
        plain[0x02E] = 0x00;
        plain[0x02F] = 0x01;
        Encoding.BigEndianUnicode.GetBytes("Lock On").CopyTo(plain, 0x038);

        var named = figure.WithPlainInternal(plain, TestData.Keys);
        Assert.Equal("Lock On", named.GetNickname(TestData.Keys));
        Assert.Equal(string.Empty, named.GetNickname(null));
        Assert.True(named.Authenticate(TestData.Keys));
        Assert.Equal(figure.Span[0x80..0xC0].ToArray(), named.Span[0x80..0xC0].ToArray());
        Assert.Equal(figure.Span[0x400..].ToArray(), named.Span[0x400..].ToArray());
    }

    [Fact]
    public void WithLockOn_StoresItInTheImageAndKeepsTheRest()
    {
        var figure = Figure();
        var withLockOn = figure.WithLockOn(LockOnPayload());

        Assert.True(withLockOn.HasLockOn);
        Assert.True(withLockOn.LockOnCrcValid);
        Assert.Equal(LockOnResponse.Normalize(LockOnPayload()), withLockOn.Span[0x3C0..0x400].ToArray());
        Assert.Equal(figure.Span[..0x3C0].ToArray(), withLockOn.Span[..0x3C0].ToArray());
        Assert.Equal(figure.Span[0x400..].ToArray(), withLockOn.Span[0x400..].ToArray());
        Assert.True(withLockOn.Authenticate(TestData.Keys));

        var removed = withLockOn.WithoutLockOn();
        Assert.False(removed.HasLockOn);
        Assert.Equal(figure, removed);
    }

    [Fact]
    public void Create_WithLockOn_IncludesItInTheBin()
    {
        var figure = Figure(LockOnPayload());
        Assert.True(figure.HasLockOn);
        Assert.Equal(LockOnResponse.Normalize(LockOnPayload()), figure.Data[0x3C0..0x400]);
        Assert.True(figure.Authenticate(TestData.Keys));
    }

    [Fact]
    public void WithUid_ReEncryptsForTheNewUidAndKeepsEverythingElse()
    {
        var figure = Figure(LockOnPayload());
        var plain = figure.Decrypt(TestData.Keys);
        plain[0x02E] = 0x00;
        plain[0x02F] = 0x01;
        Encoding.BigEndianUnicode.GetBytes("Moved").CopyTo(plain, 0x038);
        figure = figure.WithPlainInternal(plain, TestData.Keys);

        byte[] newUid = [0x04, 0x99, 0x88, 0x77, 0x66, 0x55, 0x44];
        var moved = figure.WithUid(TestData.Keys, newUid);

        Assert.Equal(newUid, moved.Uid);
        Assert.True(moved.Authenticate(TestData.Keys));
        Assert.Equal("Moved", moved.GetNickname(TestData.Keys));
        Assert.Equal(figure.LockOn, moved.LockOn);
        Assert.Equal(figure.AmiiboIdHex, moved.AmiiboIdHex);
        Assert.NotEqual(figure.Span[0x0C0..0x0E0].ToArray(), moved.Span[0x0C0..0x0E0].ToArray()); // Data HMAC changed.

        var random = figure.WithUid(TestData.Keys);
        Assert.Equal(0x04, random.Uid[0]);
        Assert.True(random.Authenticate(TestData.Keys));
    }

    [Theory]
    [InlineData(532, AmiiboFormat.Ntag215)]
    [InlineData(540, AmiiboFormat.Ntag215)]
    [InlineData(572, AmiiboFormat.Ntag215)]
    [InlineData(2048, AmiiboFormat.NtagI2cPlus2k)]
    public void Parse_PicksTheFormatBySize(int length, AmiiboFormat format)
    {
        Assert.True(AmiiboImage.TryParse(new byte[length], out var image));
        Assert.Equal(format, image!.Format);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(2047)]
    [InlineData(2049)]
    public void Parse_RejectsOtherSizes(int length)
    {
        Assert.False(AmiiboImage.TryParse(new byte[length], out _));
        Assert.Throws<TagException>(() => AmiiboImage.Parse(new byte[length]));
    }

    [Fact]
    public void QrText_RoundTripsBothFormats()
    {
        var v3 = Figure();
        Assert.Equal(v3.Data, AmiiboImage.FromQrText(v3.ToQrText()).Data);
        Assert.IsType<AmiiboV3Dump>(AmiiboImage.FromQrText(v3.ToQrText()));

        var v2 = TestData.Amiibo();
        Assert.IsType<TagDump>(AmiiboImage.FromQrText(v2.ToQrText()));
    }

    // ---------------------------------------------------------------- Simulator (datasheet behaviour)

    [Fact]
    public void Simulator_SectorSelect_PassiveAckAndNakForMissingSectors()
    {
        var tag = NtagI2cPlusSimulator.FromImage(Figure());

        Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xC2, 0xFF]).Kind);
        Assert.Equal(TagResponseKind.None, tag.Transceive([0x01, 0x00, 0x00, 0x00]).Kind);
        Assert.False(tag.IsHalted);
        Assert.Equal(1, tag.CurrentSector);

        Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xC2, 0xFF]).Kind);
        Assert.Equal(TagResponseKind.Nak, tag.Transceive([0x02, 0x00, 0x00, 0x00]).Kind);
        Assert.True(tag.IsHalted);

        tag.Activate();
        Assert.Equal(0, tag.CurrentSector);
    }

    [Theory]
    [InlineData(0xEA)]
    [InlineData(0xEB)]
    [InlineData(0xEE)]
    [InlineData(0xEF)]
    [InlineData(0xF0)]
    public void Simulator_InvalidPagesNak(int page)
    {
        var tag = NtagI2cPlusSimulator.FromImage(Figure());
        Assert.Equal(TagResponseKind.Nak, tag.Transceive([0x3A, (byte)page, (byte)page]).Kind);
    }

    [Fact]
    public void Simulator_ReadRollsOverInsideTheSector()
    {
        var figure = Figure();
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        tag.Transceive([0xC2, 0xFF]);
        tag.Transceive([0x01, 0x00, 0x00, 0x00]);

        var read = tag.Transceive([0x30, 0xFE]);
        Assert.Equal(TagResponseKind.Data, read.Kind);
        Assert.Equal(figure.GetPage(1, 0xFE), read.Data[..4]);
        Assert.Equal(figure.GetPage(1, 0x00), read.Data[8..12]);
    }

    [Fact]
    public void Simulator_PasswordAndPackReadAsZero()
    {
        var raw = Figure().Data;
        new byte[] { 1, 2, 3, 4 }.CopyTo(raw, 0xE5 * 4);
        var tag = new NtagI2cPlusSimulator(raw);
        Assert.Equal(new byte[8], tag.Transceive([0x3A, 0xE5, 0xE6]).Data);
    }

    [Fact]
    public void Simulator_WritesNeedThePasswordAndRespectLocks()
    {
        var tag = NtagI2cPlusSimulator.FromImage(Figure());

        // AUTH0 = 04: user memory is write protected.
        Assert.Equal(TagResponseKind.Nak, tag.Transceive([0xA2, 0x30, 1, 2, 3, 4]).Kind);

        tag.Activate();
        Assert.Equal(TagResponseKind.Data, tag.Transceive([0x1B, 0, 0, 0, 0]).Kind);
        Assert.Equal(TagResponseKind.Ack, tag.Transceive([0xA2, 0x30, 1, 2, 3, 4]).Kind);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, tag.GetPage(0, 0x30));

        // Page 15 (static lock) and pages 10h-1Fh (dynamic lock: ID and salt) are locked.
        Assert.True(tag.IsPageLocked(0, 0x0F));
        Assert.True(tag.IsPageLocked(0, 0x15));
        Assert.Equal(TagResponseKind.Nak, tag.Transceive([0xA2, 0x15, 1, 2, 3, 4]).Kind);

        // Sector 1 is locked completely.
        tag.Activate();
        tag.Transceive([0x1B, 0, 0, 0, 0]);
        tag.Transceive([0xC2, 0xFF]);
        tag.Transceive([0x01, 0x00, 0x00, 0x00]);
        Assert.True(tag.IsPageLocked(1, 0x80));
        Assert.Equal(TagResponseKind.Nak, tag.Transceive([0xA2, 0x80, 1, 2, 3, 4]).Kind);
    }

    // ---------------------------------------------------------------- Reading over NFC

    [Fact]
    public void Version_IsRecognised()
    {
        var version = new NtagVersion(AmiiboV3Dump.Version);
        Assert.True(version.IsNtagI2cPlus2k);
        Assert.False(version.IsNtag215);
        Assert.False(new NtagVersion(Ntag215Simulator.VersionNtag215).IsNtagI2cPlus2k);
    }

    [Fact]
    public async Task ReadAny_ReadsAV3FigureWithItsLockOn()
    {
        var figure = Figure(LockOnPayload());
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        await using var connection = new SimulatedTagConnection(tag);

        var read = Assert.IsType<AmiiboV3Dump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection));

        Assert.Equal(figure.Data, read.Data);
        Assert.Equal(figure.LockOn, read.LockOn);
        Assert.True(read.Authenticate(TestData.Keys));
        Assert.Equal(0, tag.CurrentSector);
    }

    [Fact]
    public async Task ReadAny_WithoutSramMirroring_HasNoLockOn()
    {
        var figure = Figure();
        await using var connection = new SimulatedTagConnection(NtagI2cPlusSimulator.FromImage(figure));

        var read = Assert.IsType<AmiiboV3Dump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection));

        // The session registers report pass-through off, so only EC-ED differ from the template.
        Assert.Equal(figure.Span[..(0xEC * 4)].ToArray(), read.Span[..(0xEC * 4)].ToArray());
        Assert.Equal(figure.Span[(0xEE * 4)..].ToArray(), read.Span[(0xEE * 4)..].ToArray());
        Assert.False(read.HasLockOn);
    }

    [Fact]
    public async Task ReadV3_WaitsForTheFigureToFillSram()
    {
        var figure = Figure(LockOnPayload());
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        tag.SramReadyAfterSessionReads = 4;
        await using var connection = new SimulatedTagConnection(tag);

        var read = await AmiiboV3NfcOperations.ReadV3Async(connection);

        Assert.Equal(figure.Data, read.Data);
        Assert.True(read.HasLockOn);
        Assert.True(tag.SessionReads > 4);
    }

    [Fact]
    public async Task ReadV3_GivesUpOnTheLockOnAfterTheTimeout()
    {
        var tag = NtagI2cPlusSimulator.FromImage(Figure(LockOnPayload()));
        tag.SramReadyAfterSessionReads = int.MaxValue;
        await using var connection = new SimulatedTagConnection(tag);

        var read = await AmiiboV3NfcOperations.ReadV3Async(connection, lockOnTimeout: TimeSpan.FromMilliseconds(200));

        Assert.False(read.HasLockOn);
        Assert.True(read.HasAmiiboId);
        Assert.DoesNotContain(tag.CommandLog, c => c[0] == 0x3A && c[1] >= 0xF0); // Never tried an unready SRAM.
    }

    [Fact]
    public async Task ReadAny_WithReadOnly_StillAvoidsInvalidPages()
    {
        var figure = Figure();
        var tag = NtagI2cPlusSimulator.FromImage(figure);
        await using var connection = new SimulatedTagConnection(tag, unsupportedCommands: [0x3A]);

        var read = Assert.IsType<AmiiboV3Dump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection));

        // E8-E9 and EC-ED need FAST_READ, so they stay zero; everything the amiibo needs is there.
        Assert.Equal(figure.Span[..(0xE8 * 4)].ToArray(), read.Span[..(0xE8 * 4)].ToArray());
        Assert.Equal(figure.Span[0x400..].ToArray(), read.Span[0x400..].ToArray());
        Assert.True(read.Authenticate(TestData.Keys));
    }

    [Fact]
    public async Task ReadAny_StillReadsNtag215()
    {
        await using var connection = new SimulatedTagConnection(TestData.LockedAmiiboTag());
        var read = Assert.IsType<TagDump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection));
        Assert.Equal(TestData.MarioId, read.AmiiboIdHex);
    }

    [Fact]
    public async Task ReadV3_ThroughTheMfrc522Bridge()
    {
        var figure = Figure(LockOnPayload());
        var bridge = new SimulatedMfrc522Bridge(NtagI2cPlusSimulator.FromImage(figure));
        var provider = new Mfrc522NfcProvider(new SimulatedSerialPortFactory(bridge));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await provider.ConnectAsync(new NfcConnectOptions(), cts.Token);

        var read = Assert.IsType<AmiiboV3Dump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection, cancellationToken: cts.Token));
        Assert.Equal(figure.Data, read.Data);
        Assert.Equal(figure.LockOn, read.LockOn);
    }
}
