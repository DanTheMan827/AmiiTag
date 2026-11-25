using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;

namespace AmiiTag.Tests;

/// <summary>
/// The MFRC522 driven directly over its UART (no microcontroller): register access, finding the chip and its baud
/// rate, RF setup, anticollision, and every amiibo command through the FIFO, against a register level simulation.
/// </summary>
public class Mfrc522Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static (Mfrc522NfcProvider Provider, SimulatedSerialPortFactory Ports, SimulatedMfrc522Bridge Chip) Create(TagSimulator? tag)
    {
        var chip = new SimulatedMfrc522Bridge(tag);
        var ports = new SimulatedSerialPortFactory(chip);
        return (new Mfrc522NfcProvider(ports), ports, chip);
    }

    private static async Task<INfcTagConnection> ConnectAsync(Mfrc522NfcProvider provider)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await provider.ConnectAsync(new NfcConnectOptions(), cts.Token);
    }

    private static async Task<Mfrc522Reader> OpenReaderAsync(SimulatedMfrc522Bridge chip, int baud = Mfrc522NfcProvider.PowerOnBaudRate)
    {
        var ports = new SimulatedSerialPortFactory(chip);
        var reader = new Mfrc522Reader(new Mfrc522Uart(await ports.OpenAsync(ports.PortName, baud), baud));
        await reader.InitAsync();
        return reader;
    }

    // ---------------------------------------------------------------- UART and setup

    [Fact]
    public async Task Uart_ReadsAndWritesRegisters()
    {
        var chip = new SimulatedMfrc522Bridge();
        var uart = new Mfrc522Uart(await new SimulatedSerialPortFactory(chip).OpenAsync("COM7", 9600), 9600);

        Assert.Equal(0x92, await uart.ReadAsync(Mfrc522Reg.Version));
        await uart.WriteManyAsync([(Mfrc522Reg.TPrescaler, 0x12), (Mfrc522Reg.TReloadL, 0x34)]);
        Assert.Equal(new byte[] { 0x12, 0x34 }, await uart.ReadManyAsync([Mfrc522Reg.TPrescaler, Mfrc522Reg.TReloadL]));
    }

    [Fact]
    public async Task Uart_AtTheWrongBaudRate_TimesOut()
    {
        var chip = new SimulatedMfrc522Bridge();
        var uart = new Mfrc522Uart(await new SimulatedSerialPortFactory(chip).OpenAsync("COM7", 115200), 115200);
        await Assert.ThrowsAsync<TimeoutException>(() => uart.ReadAsync(Mfrc522Reg.Version));
    }

    [Fact]
    public async Task Init_ConfiguresTheTimerAndTurnsTheAntennaOn()
    {
        var chip = new SimulatedMfrc522Bridge();
        await OpenReaderAsync(chip);

        Assert.True(chip.AntennaOn);
        Assert.Equal(Mfrc522Reg.TAuto, chip.GetRegister(Mfrc522Reg.TMode));
        Assert.Equal(0xA9, chip.GetRegister(Mfrc522Reg.TPrescaler));
        Assert.Equal(0x03, chip.GetRegister(Mfrc522Reg.TReloadH));
        Assert.Equal(0xE8, chip.GetRegister(Mfrc522Reg.TReloadL));
        Assert.Equal(0x40, chip.GetRegister(Mfrc522Reg.TxAsk));
        Assert.Equal(0x3D, chip.GetRegister(Mfrc522Reg.Mode));
        Assert.Equal(0x00, chip.GetRegister(Mfrc522Reg.TxMode)); // CRC is handled by the host.
    }

    [Fact]
    public async Task Connect_FindsTheChipAtPowerOnRate_AndSwitchesTo921600()
    {
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        await using var connection = await ConnectAsync(provider);

        Assert.Equal(921600, chip.ChipBaudRate);
        Assert.Equal(921600, ports.LastBaudRate);
        Assert.Equal(new[] { 921600, 9600, 921600 }, ports.OpenedBaudRates);
        Assert.Equal((byte)0x92, provider.ChipVersion);
    }

    [Fact]
    public async Task Release_PutsTheChipBackAt9600WithTheFieldOff()
    {
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        await using (await ConnectAsync(provider))
        {
        }

        Assert.Equal(921600, chip.ChipBaudRate);
        Assert.True(chip.AntennaOn);

        await provider.ReleaseAsync();
        Assert.Equal(9600, chip.ChipBaudRate);
        Assert.False(chip.AntennaOn);
        Assert.False(chip.IsOpen);

        // Next time it's found at the power-on rate again and switched up.
        await using var again = await ConnectAsync(provider);
        Assert.Equal(new[] { 921600, 9600, 921600, 921600, 9600, 921600 }, ports.OpenedBaudRates);
    }

    [Fact]
    public async Task Connect_UsesTheFastRateDirectlyWhenTheChipIsAlreadyThere()
    {
        // E.g. the app was closed without releasing the reader: the chip is still at 921600.
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        var setup = new Mfrc522Uart(await ports.OpenAsync(ports.PortName, 9600), 9600);
        await setup.Port.WriteAsync([Mfrc522Reg.SerialSpeed, 0x1C]);
        Assert.Equal(921600, chip.ChipBaudRate);
        ports.OpenedBaudRates.Clear();

        await using var connection = await ConnectAsync(provider);
        Assert.Equal(new[] { 921600 }, ports.OpenedBaudRates);
    }

    [Fact]
    public async Task Connect_FindsAChipLeftAtAnotherRate_AndSwitchesItToTheChosenOne()
    {
        // E.g. an earlier session ran it at 115200 and didn't release it.
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        var setup = new Mfrc522Uart(await ports.OpenAsync(ports.PortName, 9600), 9600);
        await setup.Port.WriteAsync([Mfrc522Reg.SerialSpeed, 0x7A]);
        ports.OpenedBaudRates.Clear();

        await using var connection = await ConnectAsync(provider);

        Assert.Contains(115200, ports.OpenedBaudRates);
        Assert.Equal(921600, chip.ChipBaudRate);
        Assert.Equal(921600, ports.LastBaudRate);
    }

    [Fact]
    public async Task Connect_UsesTheSpeedChosenInSettings()
    {
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        provider.BaudRate = 230400;
        await using var connection = await ConnectAsync(provider);

        Assert.Equal(230400, chip.ChipBaudRate);
        Assert.Equal(230400, ports.LastBaudRate);
    }

    [Fact]
    public void BaudRate_MustBeOneTheChipSupports()
    {
        var (provider, _, _) = Create(null);
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.BaudRate = 250000);
        Assert.Equal(Mfrc522NfcProvider.FastBaudRate, ((INfcBaudRateSetting)provider).DefaultBaudRate);
    }

    [Fact]
    public async Task Connect_StaysAt9600WhenAskedTo()
    {
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        provider.BaudRate = 9600;
        await using var connection = await ConnectAsync(provider);
        Assert.Equal(9600, chip.ChipBaudRate);
        Assert.Equal(new[] { 9600 }, ports.OpenedBaudRates);
    }

    [Fact]
    public async Task Connect_NothingOnThePort_ReportsItWithoutMentioningFirmware()
    {
        var (provider, _, chip) = Create(Ntag215Simulator.CreateBlank());
        chip.ChipConnected = false;

        var error = await Assert.ThrowsAsync<NfcReaderNotFoundException>(() => ConnectAsync(provider));
        Assert.Contains("No MFRC522 answered on COM7", error.Message);
        Assert.DoesNotContain("firmware", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("flash", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connect_UnknownChipVersion_IsNotAccepted()
    {
        var (provider, _, chip) = Create(Ntag215Simulator.CreateBlank());
        chip.Version = 0x00;
        await Assert.ThrowsAsync<NfcReaderNotFoundException>(() => ConnectAsync(provider));
    }

    // ---------------------------------------------------------------- Finding tags

    [Fact]
    public async Task Connect_SelectsTheTagThroughBothCascadeLevels()
    {
        var tag = Ntag215Simulator.CreateBlank(TestData.Uid7(0x30));
        var (provider, _, chip) = Create(tag);
        await using var connection = await ConnectAsync(provider);

        Assert.Equal(TestData.Uid7(0x30), connection.Uid);
        Assert.Contains(chip.AirFrames, f => f.Length == 1 && f[0] == 0x52); // WUPA
        Assert.Contains(chip.AirFrames, f => f.Length == 2 && f[0] == 0x93 && f[1] == 0x20);
        Assert.Contains(chip.AirFrames, f => f.Length == 2 && f[0] == 0x95 && f[1] == 0x20);
    }

    [Fact]
    public async Task Select_WakesAHaltedTag()
    {
        var tag = Ntag215Simulator.CreateBlank();
        tag.Halt();
        var reader = await OpenReaderAsync(new SimulatedMfrc522Bridge(tag));

        var card = await reader.SelectAsync();
        Assert.NotNull(card);
        Assert.Equal(new byte[] { 0x44, 0x00 }, card!.Atqa);
        Assert.Equal(0x00, card.Sak);
        Assert.False(tag.IsHalted);
    }

    [Fact]
    public async Task Select_WithoutATag_ReturnsNull()
    {
        var reader = await OpenReaderAsync(new SimulatedMfrc522Bridge());
        Assert.Null(await reader.SelectAsync());
    }

    [Fact]
    public async Task Connect_WaitsForATagToArrive()
    {
        var (provider, _, chip) = Create(null);
        var connecting = ConnectAsync(provider);
        await Task.Delay(600);
        Assert.False(connecting.IsCompleted);

        chip.Tag = Ntag215Simulator.CreateBlank();
        await using var connection = await connecting;
        Assert.Equal(7, connection.Uid.Length);
    }

    [Fact]
    public async Task Connect_CanBeCancelledWhileWaiting()
    {
        var (provider, _, _) = Create(null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ConnectAsync(new NfcConnectOptions(), cts.Token));
    }

    [Fact]
    public async Task Close_HaltsTheTag()
    {
        var tag = Ntag215Simulator.CreateBlank();
        var (provider, _, _) = Create(tag);
        var connection = await ConnectAsync(provider);
        await connection.CloseAsync();
        Assert.True(tag.IsHalted);
    }

    [Fact]
    public async Task Connect_ReopensAClosedPort()
    {
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        await (await ConnectAsync(provider)).CloseAsync();
        var opens = ports.OpenCount;

        await chip.DisposeAsync(); // Unplugged and plugged back in: the old handle is dead.
        await using var again = await ConnectAsync(provider);

        Assert.True(ports.OpenCount > opens);
        Assert.True(chip.IsOpen);
        Assert.Equal(16, (await again.TransceiveAsync([0x30, 0x00])).Length);
    }

    [Fact]
    public async Task Connect_FindsTheChipAgainAfterItLostPower()
    {
        var (provider, ports, chip) = Create(Ntag215Simulator.CreateBlank());
        await (await ConnectAsync(provider)).CloseAsync();
        Assert.Equal(921600, chip.ChipBaudRate);

        chip.PowerOn(); // Back at 9600 baud with the antenna off.
        await using var again = await ConnectAsync(provider);

        Assert.Equal(921600, chip.ChipBaudRate);
        Assert.True(chip.AntennaOn);
        Assert.Equal(16, (await again.TransceiveAsync([0x30, 0x00])).Length);
    }

    [Fact]
    public async Task Connect_ReinitialisesAChipThatResetAtTheSameBaudRate()
    {
        var (provider, _, chip) = Create(Ntag215Simulator.CreateBlank());
        provider.BaudRate = 9600;
        await (await ConnectAsync(provider)).CloseAsync();

        chip.PowerOn(); // Still 9600 baud, but the antenna is off and the timer settings are gone.
        await using var again = await ConnectAsync(provider);

        Assert.True(chip.AntennaOn);
        Assert.Equal(0xA9, chip.GetRegister(Mfrc522Reg.TPrescaler));
    }

    [Fact]
    public async Task Connect_TagLeftHaltedOnTheReader_IsFoundAfterAFieldReset()
    {
        // Close() halts the tag. Some real tags then ignore WUPA until they lose power: writing to a tag that was left on
        // the reader must still find it.
        var tag = Ntag215Simulator.CreateBlank();
        var (provider, _, chip) = Create(tag);
        chip.HaltedTagIgnoresWupa = true;

        await (await ConnectAsync(provider)).CloseAsync();
        Assert.True(tag.IsHalted);
        var resetsBefore = chip.FieldResets;

        await using var again = await ConnectAsync(provider);
        Assert.True(chip.FieldResets > resetsBefore);
        Assert.Equal(16, (await again.TransceiveAsync([0x30, 0x00])).Length);
    }

    [Fact]
    public async Task Connect_ReusesTheOpenPortForTheNextTag()
    {
        var (provider, ports, _) = Create(Ntag215Simulator.CreateBlank());
        await (await ConnectAsync(provider)).CloseAsync();
        var opens = ports.OpenCount;
        await using var again = await ConnectAsync(provider);
        Assert.Equal(opens, ports.OpenCount);
    }

    // ---------------------------------------------------------------- Amiibo commands through the FIFO

    [Fact]
    public async Task ReadTag_MatchesTheTag()
    {
        var dump = TestData.Amiibo();
        var (provider, _, _) = Create(Ntag215Simulator.FromAmiiboDump(dump));
        await using var connection = await ConnectAsync(provider);

        var read = await AmiiboNfcOperations.ReadTagAsync(connection);
        Assert.Equal(dump.Span[..TagDump.ShortLength].ToArray(), read.Span[..TagDump.ShortLength].ToArray());
        Assert.Equal(dump.Signature, read.Signature);
    }

    [Fact]
    public async Task WriteTag_Blank_ProducesALockedAmiibo()
    {
        var tag = Ntag215Simulator.CreateBlank(TestData.Uid7(0x44));
        var (provider, _, _) = Create(tag);
        await using var connection = await ConnectAsync(provider);

        var result = await AmiiboNfcOperations.WriteTagAsync(connection, TestData.Amiibo(), TestData.Keys);

        Assert.Equal(NfcWriteKind.FullWrite, result);
        Assert.True(tag.IsPageLocked(0x10)); // Dynamic lock: ID and salt pages.
        Assert.Equal(NtagUid.AmiiboPassword(TestData.Uid9(0x44)), tag.GetPage(Ntag215Simulator.PwdPage));
    }

    [Fact]
    public async Task WriteTag_AppData_AuthenticatesFirst()
    {
        var dump = TestData.Amiibo();
        var tag = Ntag215Simulator.FromAmiiboDump(dump);
        var (provider, _, _) = Create(tag);
        await using var connection = await ConnectAsync(provider);

        Assert.Equal(NfcWriteKind.AppData, await AmiiboNfcOperations.WriteTagAsync(connection, dump, keys: null));
        var commands = tag.CommandLog.Select(c => c[0]).ToList();
        Assert.True(commands.IndexOf(Ntag215Simulator.CmdPwdAuth) < commands.IndexOf(Ntag215Simulator.CmdWrite));
    }

    [Fact]
    public async Task Write_AckIsReturnedAsTheFourBitValue()
    {
        var (provider, _, _) = Create(Ntag215Simulator.CreateBlank());
        await using var connection = await ConnectAsync(provider);
        Assert.Equal(new byte[] { 0x0A }, await connection.TransceiveAsync([0xA2, 0x10, 1, 2, 3, 4]));
    }

    [Fact]
    public async Task Write_ToALockedPage_ReturnsTheNak()
    {
        var (provider, _, _) = Create(TestData.LockedAmiiboTag());
        await using var connection = await ConnectAsync(provider);
        Assert.Equal(new byte[] { TagResponse.NakInvalidArgument }, await connection.TransceiveAsync([0xA2, 0x00, 1, 2, 3, 4]));
        await Assert.ThrowsAsync<NfcException>(() => new Ntag215Client(connection).WritePageAsync(0x04, new byte[4]));
    }

    [Fact]
    public async Task EveryAmiiboCommand_Works()
    {
        var dump = TestData.Amiibo();
        var tag = Ntag215Simulator.FromAmiiboDump(dump);
        var (provider, _, _) = Create(tag);
        await using var connection = await ConnectAsync(provider);
        var client = new Ntag215Client(connection);

        Assert.True((await client.GetVersionAsync())!.IsNtag215);
        Assert.Equal(dump.Signature, await client.ReadSignatureAsync());
        Assert.Equal(dump.Span[..16].ToArray(), await connection.TransceiveAsync([0x30, 0x00]));
        Assert.Equal(dump.Span[..60].ToArray(), await connection.TransceiveAsync([0x3A, 0x00, 0x0E]));
        Assert.Equal(new byte[] { 0x80, 0x80 }, await client.PasswordAuthAsync(NtagUid.AmiiboPassword(dump.Uid)));
        await client.WritePageAsync(0x20, new byte[] { 1, 2, 3, 4 });
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, tag.GetPage(0x20));
    }

    [Fact]
    public async Task FastRead_PastTheFifoSize_IsReportedAsAnOverflow()
    {
        var (provider, _, _) = Create(Ntag215Simulator.CreateBlank());
        await using var connection = await ConnectAsync(provider);
        var error = await Assert.ThrowsAsync<NfcException>(() => connection.TransceiveAsync([0x3A, 0x00, 0x0F])); // 16 pages + CRC = 66 bytes.
        Assert.Contains("FIFO", error.Message);
    }

    [Fact]
    public async Task CorruptedResponseCrc_IsReported()
    {
        var (provider, _, chip) = Create(Ntag215Simulator.CreateBlank());
        await using var connection = await ConnectAsync(provider);
        chip.CorruptNextTagCrc = true;
        var error = await Assert.ThrowsAsync<NfcException>(() => connection.TransceiveAsync([0x30, 0x00]));
        Assert.Contains("CRC", error.Message);
    }

    [Fact]
    public async Task TagRemoved_ReportsThatTheTagDidNotRespond()
    {
        var (provider, _, chip) = Create(Ntag215Simulator.CreateBlank());
        await using var connection = await ConnectAsync(provider);
        chip.Tag = null;
        var error = await Assert.ThrowsAsync<NfcException>(() => connection.TransceiveAsync([0x30, 0x00]));
        Assert.Contains("didn't respond", error.Message);
    }

    [Fact]
    public async Task AfterANak_TheTagIsSilentUntilSelectedAgain()
    {
        var tag = TestData.LockedAmiiboTag();
        var reader = await OpenReaderAsync(new SimulatedMfrc522Bridge(tag));
        Assert.NotNull(await reader.SelectAsync());

        Assert.Equal(new byte[] { TagResponse.NakInvalidArgument }, await reader.TransceiveAsync([0xA2, 0x04, 0, 0, 0, 0]));
        await Assert.ThrowsAsync<NfcException>(() => reader.TransceiveAsync([0x30, 0x00]));

        Assert.NotNull(await reader.SelectAsync());
        Assert.Equal(16, (await reader.TransceiveAsync([0x30, 0x00])).Length);
    }

    [Fact]
    public async Task AntennaOff_NoTagAnswers()
    {
        var reader = await OpenReaderAsync(new SimulatedMfrc522Bridge(Ntag215Simulator.CreateBlank()));
        await reader.AntennaOffAsync();
        Assert.Null(await reader.SelectAsync());
    }

    [Fact]
    public void Crc_A_MatchesTheIso14443HaltCommand()
    {
        Assert.Equal(new byte[] { 0x57, 0xCD }, Iso14443Crc.Compute([0x50, 0x00]));
        Assert.Equal(Crc14443A.Compute([0x30, 0x04]), Iso14443Crc.Compute([0x30, 0x04]));
    }
}
