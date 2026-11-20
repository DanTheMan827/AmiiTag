using System.Threading.Channels;
using AmiiTag.Nfc;
using AmiiTag.Nfc.Pn532;

namespace AmiiTag.Tests;

public sealed class Pn532Tests
{
    [Fact]
    public async Task FirmwareQuery_UsesDocumentedFrame_AndAcceptsFragmentedReplies()
    {
        var port = new Chip();
        await using var uart = new Pn532Uart(port);
        Assert.Equal(new byte[] { 0x32, 1, 6, 7 }, await uart.CommandAsync(2, []));
        Assert.Equal(new byte[] { 0, 0, 0xFF, 2, 0xFE, 0xD4, 2, 0x2A, 0 }, port.Writes.Single());
    }

    [Fact]
    public async Task LowVbatStartup_SendsLongPreambleAndSamInOneWrite()
    {
        var port = new Chip { LowVbat = true };
        await using var uart = new Pn532Uart(port);
        await uart.WakeAsync();
        await uart.CommandAsync(0x14, [1, 0x14, 0]);
        Assert.Equal(1, port.Writes.Count);
        Assert.Equal(new byte[] { 0x55, 0x55 }, port.Writes[0][..2]);
        Assert.Equal(new byte[14], port.Writes[0][2..16]);
        Assert.Equal(new byte[] { 0, 0, 0xFF, 5, 0xFB, 0xD4, 0x14, 1, 0x14, 0, 3, 0 }, port.Writes[0][16..]);
        Assert.True(!port.LowVbat);
    }

    [Fact]
    public async Task ChosenBaudRate_IsSwitchedToAfterConnecting_AndBackOnRelease()
    {
        var ports = new Ports();
        var chips = new List<Chip>();
        ports.CreateChip = () =>
        {
            var chip = new Chip { LowVbat = chips.Count == 0 };
            chips.Add(chip);
            return chip;
        };
        await using var provider = new Pn532NfcProvider(ports) { BaudRate = 921600 };

        await using (await provider.ConnectAsync(new NfcConnectOptions()))
        {
            // SetSerialBaudRate (BR 7 = 921600) at 115200, confirmed with the host's ACK, then the port reopened.
            Assert.Contains(chips[0].Commands, c => c.Command == 0x10 && c.Data.SequenceEqual(new byte[] { 7 }));
            Assert.Contains(chips[0].Writes, w => w.SequenceEqual(new byte[] { 0, 0, 0xFF, 0, 0xFF, 0 }));
            Assert.Equal(2, chips.Count);
            Assert.Equal(921600, ports.LastRate);
            Assert.Equal(921600, provider.LinkBaudRate);
        }

        await provider.ReleaseAsync();
        Assert.Contains(chips[1].Commands, c => c.Command == 0x10 && c.Data.SequenceEqual(new byte[] { 4 }));
        Assert.Equal(115200, provider.LinkBaudRate);
    }

    [Fact]
    public async Task ChosenBaudRate_IsSwitchedTo_AfterTheAutoDetectionProbe()
    {
        var ports = new Ports();
        var chips = new List<Chip>();
        ports.CreateChip = () =>
        {
            var chip = new Chip();
            chips.Add(chip);
            return chip;
        };
        await using var provider = new Pn532NfcProvider(ports) { BaudRate = 460800 };

        Assert.True(await provider.ProbeAsync("COM7"));
        Assert.Equal(115200, provider.LinkBaudRate);

        // The probe's port is reused, then switched (BR 6 = 460800).
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions { ReaderId = "COM7" });
        Assert.Contains(chips[0].Commands, c => c.Command == 0x10 && c.Data.SequenceEqual(new byte[] { 6 }));
        Assert.Equal(460800, ports.LastRate);
        Assert.Equal(460800, provider.LinkBaudRate);
    }

    [Fact]
    public async Task Release_AfterACancelledConnect_StillRestoresThePowerOnRate()
    {
        var ports = new Ports();
        var chips = new List<Chip>();
        ports.CreateChip = () =>
        {
            var chip = new Chip { EmptyPolls = int.MaxValue };
            chips.Add(chip);
            return chip;
        };
        await using var provider = new Pn532NfcProvider(ports) { BaudRate = 921600 };

        // Cancelled while polling for a tag (Ctrl+C): the port is closed with the PN532 still at 921600.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ConnectAsync(new NfcConnectOptions(), cancel.Token));
        Assert.Equal(921600, provider.LinkBaudRate);

        // Release reopens it at that rate to switch it back (BR 4 = 115200).
        await provider.ReleaseAsync();
        Assert.Equal(921600, ports.LastRate);
        Assert.Contains(chips[^1].Commands, c => c.Command == 0x10 && c.Data.SequenceEqual(new byte[] { 4 }));
        Assert.Equal(115200, provider.LinkBaudRate);
    }

    [Fact]
    public async Task Reconnect_WakesModuleThatResetWithoutDisconnectingAdapter()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using (await provider.ConnectAsync(new NfcConnectOptions())) { }
        var chip = ports.Last;
        chip.LowVbat = true;
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        Assert.True(ReferenceEquals(chip, ports.Last));
        Assert.True(!chip.LowVbat);
    }

    [Fact]
    public async Task Timeout_DistinguishesMissingAckFromMissingCommandResponse()
    {
        var port = new Chip { Silent = true };
        await using var uart = new Pn532Uart(port);
        var noAck = await Assert.ThrowsAsync<TimeoutException>(() => uart.CommandAsync(2, [], TimeSpan.FromMilliseconds(30)));
        Assert.True(noAck.Message.Contains("waiting for ACK; 0 UART bytes received"));
        var ackOnly = new Chip { AckOnly = true };
        await using var uart2 = new Pn532Uart(ackOnly);
        var noResponse = await Assert.ThrowsAsync<TimeoutException>(() => uart2.CommandAsync(2, [], TimeSpan.FromMilliseconds(30)));
        Assert.True(noResponse.Message.Contains("waiting for command response; 6 UART bytes received"));
    }

    [Fact]
    public async Task Uart_HandlesExtendedRequestsAndReplies()
    {
        var port = new Chip { Handler = (_, data) => data };
        await using var uart = new Pn532Uart(port);
        var data = Enumerable.Range(0, 264).Select(i => (byte)i).ToArray();
        Assert.Equal(data, await uart.CommandAsync(0, data));
        Assert.Equal(0xFF, port.Writes.Single()[3]);
    }

    [Fact]
    public async Task BadChecksum_ClosesStreamInsteadOfReusingLateReplies()
    {
        var port = new Chip { CorruptChecksum = true };
        await using var uart = new Pn532Uart(port);
        await Assert.ThrowsAsync<NfcException>(() => uart.CommandAsync(2, []));
        Assert.True(port.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => uart.CommandAsync(2, []));
    }

    [Fact]
    public async Task BadLengthChecksum_IsRejected()
    {
        var port = new Chip { ResponseOverride = [0, 0, 0xFF, 2, 0, 0, 0] };
        await using var uart = new Pn532Uart(port);
        await Assert.ThrowsAsync<NfcException>(() => uart.CommandAsync(2, []));
        Assert.True(port.Closed);
    }

    [Fact]
    public async Task WrongResponseCommand_IsRejected()
    {
        var port = new Chip { ResponseCommand = 0x09 };
        await using var uart = new Pn532Uart(port);
        await Assert.ThrowsAsync<NfcException>(() => uart.CommandAsync(2, []));
    }

    [Fact]
    public async Task Nack_IsRejected()
    {
        var port = new Chip { Ack = [0, 0, 0xFF, 0xFF, 0, 0] };
        await using var uart = new Pn532Uart(port);
        await Assert.ThrowsAsync<NfcException>(() => uart.CommandAsync(2, []));
    }

    [Fact]
    public async Task HostTimeout_ClosesPort()
    {
        var port = new Chip { Silent = true };
        await using var uart = new Pn532Uart(port);
        await Assert.ThrowsAsync<TimeoutException>(() => uart.CommandAsync(2, [], TimeSpan.FromMilliseconds(30)));
        Assert.True(port.Closed);
    }

    [Fact]
    public async Task Cancellation_ClosesPort()
    {
        var port = new Chip { Silent = true };
        await using var uart = new Pn532Uart(port);
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uart.CommandAsync(2, [], cancellationToken: cts.Token));
        Assert.True(port.Closed);
    }

    [Fact]
    public async Task Connect_InitializesAt115200_AndReportsUid()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(115200, ports.LastRate);
        Assert.Equal(Chip.Uid, tag.Uid);
        Assert.Equal(new byte[] { 0x32, 1, 6, 7 }, provider.FirmwareVersion);
        Assert.Contains(ports.Last.Commands, c => c.Command == 0x32 && c.Data.SequenceEqual(new byte[] { 5, 0xFF, 1, 0 }));
        Assert.Contains(ports.Last.Commands, c => c.Command == 0x32 && c.Data.SequenceEqual(new byte[] { 4, 0 }));
    }

    [Fact]
    public async Task Connect_PollsUntilTagAppears()
    {
        var ports = new Ports();
        ports.CreateChip = () => new Chip { EmptyPolls = 1 };
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(2, ports.Last.Commands.Count(c => c.Command == 0x4A));
    }

    [Fact]
    public async Task ReadAndWrite_UseRawCommandsWithCrc_AndNibbleAck()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(new byte[] { 0x0A }, await tag.TransceiveAsync([0xA2, 4, 1, 2, 3, 4]));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (await tag.TransceiveAsync([0x30, 4]))[..4]);
        // UART carries bare NFC commands; the PN532's transmit CRC generator appends CRC exactly once on air.
        Assert.Equal(new byte[] { 0xA2, 4, 1, 2, 3, 4 }, ports.Last.Commands.First(c => c.Command == 0x42).Data);
        Assert.All(ports.Last.AirFrames, frame => Assert.True(Iso14443Crc.Check(frame)));
    }

    [Fact]
    public async Task FastRead_HandlesExtendedReplyAtAdvertisedLimit()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        var data = await tag.TransceiveAsync([0x3A, 0, (byte)(tag.MaxFastReadPages - 1)]);
        Assert.Equal(252, data.Length);
        Assert.Equal(ports.Last.Memory[..252], data);
    }

    [Fact]
    public async Task Nak_IsNotMistakenForAck()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfResponse = [0, 4];
        var error = await Assert.ThrowsAsync<NfcNakException>(() => tag.TransceiveAsync([0xA2, 4, 0, 0, 0, 0]));
        Assert.Equal((byte)4, error.Nak);
    }

    [Fact]
    public async Task InvalidRfCrc_IsRejected()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfResponse = [0, 1, 2, 3, 4];
        await Assert.ThrowsAsync<NfcException>(() => tag.TransceiveAsync([0x30, 4]));
    }

    [Fact]
    public async Task Send_AllowsOnlyRfSilence_NotRfErrorsOrHostTimeout()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfResponse = [1];
        await tag.SendAsync([0, 0, 0, 0]);
        await Assert.ThrowsAsync<NfcException>(() => tag.TransceiveAsync([0x30, 4]));
        ports.Last.RfResponse = [2];
        await Assert.ThrowsAsync<NfcException>(() => tag.SendAsync([0, 0, 0, 0]));
        ports.Last.Silent = true;
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tag.SendAsync([0, 0, 0, 0], cts.Token));
        Assert.True(ports.Last.Closed);
    }

    [Fact]
    public async Task TemporaryTimeout_IsAppliedAndRestored()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        await tag.TransceiveAsync([0x30, 4], TimeSpan.FromMilliseconds(400));
        var changes = ports.Last.Commands.Where(c => c.Command == 0x32 && c.Data[0] == 2).ToArray();
        Assert.Equal(new byte[] { 2, 0, 0x0B, 0x0D }, changes[^2].Data);
        Assert.Equal(new byte[] { 2, 0, 0x0B, 0x0B }, changes[^1].Data);
    }

    [Fact]
    public async Task Reselect_UsesStoredTargetWithoutFieldReset()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.Commands.Clear();
        await ((INfcReselectable)tag).ReselectAsync();
        Assert.Equal(new byte[] { 0x08, 0x44, 0x54, 0x08 }, ports.Last.Commands.Select(c => c.Command).ToArray());
        Assert.Equal(new byte[] { 1 }, ports.Last.Commands[2].Data);
    }

    [Fact]
    public async Task Release_TurnsOffField_ClosesPort_AndAllowsReopen()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var oldTag = await provider.ConnectAsync(new NfcConnectOptions());
        var oldChip = ports.Last;
        await provider.ReleaseAsync();
        Assert.True(oldChip.Closed);
        Assert.Contains(oldChip.Commands, c => c.Command == 0x32 && c.Data.SequenceEqual(new byte[] { 1, 0 }));
        await using var newTag = await provider.ConnectAsync(new NfcConnectOptions());
        await oldTag.CloseAsync(); // Must not close the new tag session.
        Assert.Equal(16, (await newTag.TransceiveAsync([0x30, 4])).Length);
        await Assert.ThrowsAsync<NfcConnectionLostException>(() => oldTag.TransceiveAsync([0x30, 4]));
    }

    [Fact]
    public async Task InvalidFirmware_ClosesThePort()
    {
        var ports = new Ports { CreateChip = () => new Chip { Firmware = [0x33, 1, 6, 7] } };
        await using var provider = new Pn532NfcProvider(ports);
        await Assert.ThrowsAsync<NfcException>(() => provider.ConnectAsync(new NfcConnectOptions()));
        Assert.True(ports.Last.Closed);
    }

    [Fact]
    public async Task BackgroundConnect_NeverRequestsAPort()
    {
        var ports = new Ports { Granted = false };
        await using var provider = new Pn532NfcProvider(ports);
        await Assert.ThrowsAsync<NfcException>(() => provider.ConnectAsync(new NfcConnectOptions { AllowUserPrompts = false }));
        Assert.Equal(0, ports.Requests);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(1, ports.Requests);
    }

    [Fact]
    public async Task CancelledConnect_ClosesPort_AndNextConnectReinitializes()
    {
        var ports = new Ports { CreateChip = () => new Chip { EmptyPolls = int.MaxValue } };
        await using var provider = new Pn532NfcProvider(ports);
        using var cts = new CancellationTokenSource(180);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ConnectAsync(new NfcConnectOptions(), cts.Token));
        Assert.True(ports.Last.Closed);
        ports.CreateChip = () => new Chip();
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        Assert.Equal(Chip.Uid, tag.Uid);
    }

    [Fact]
    public async Task Close_EmitsCrcProtectedHalt_AfterRawRead()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        var tag = await provider.ConnectAsync(new NfcConnectOptions());
        await tag.TransceiveAsync([0x30, 4]);
        await tag.CloseAsync();
        Assert.Equal(new byte[] { 0x50, 0x00, 0x57, 0xCD }, ports.Last.AirFrames.Last());
        Assert.True(ports.Last.Halted);
    }

    [Fact]
    public async Task RawRead_HardwareAppendsCrcExactlyOnce()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        await tag.TransceiveAsync([0x30, 4]);
        Assert.Equal(new byte[] { 0x30, 4 }, ports.Last.Commands.Last(c => c.Command == 0x42).Data);
        Assert.Equal(Iso14443Crc.Append(new byte[] { 0x30, 4 }), ports.Last.AirFrames.Last());
        Assert.Equal(4, ports.Last.AirFrames.Last().Length);
    }

    [Fact]
    public async Task Reselect_RestoresFirmwareCrcMode_ThenRawAckMode()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        await tag.TransceiveAsync([0x30, 4]);
        ports.Last.TxMode = 0; // Simulate stale raw state left by the previous driver.
        ports.Last.RxMode = 0;
        ports.Last.BitFraming = 7;
        await ((INfcReselectable)tag).ReselectAsync();
        Assert.Equal(new byte[] { 0x50, 0x00, 0x57, 0xCD }, ports.Last.AirFrames.Last());
        Assert.Equal(0x80, ports.Last.TxMode);
        Assert.Equal(0, ports.Last.RxMode);
        Assert.Equal(0, ports.Last.BitFraming);
        Assert.Equal(new byte[] { 0x0A }, await tag.TransceiveAsync([0xA2, 4, 1, 2, 3, 4]));
    }

    [Fact]
    public async Task HeldTag_ReadAgainAfterClose_WakesHaltedTag()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using (await provider.ConnectAsync(new NfcConnectOptions())) { }
        Assert.True(ports.Last.Halted);
        using var deadline = new CancellationTokenSource(1000);
        await using var again = await provider.ConnectAsync(new NfcConnectOptions(), deadline.Token);
        Assert.Equal(16, (await again.TransceiveAsync([0x30, 4])).Length);
        Assert.Contains(ports.Last.Commands, c => c.Command == 0x4A && c.Data.SequenceEqual(new byte[] { 1, 0, 0x88, 4, 1, 2, 3, 4, 5, 6 }));
    }

    [Fact]
    public async Task HeldTag_CancelledBackgroundPoll_DoesNotForgetWakeUid()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using (await provider.ConnectAsync(new NfcConnectOptions())) { }
        ports.Last.EmptyPolls = int.MaxValue;
        using (var monitorCancellation = new CancellationTokenSource(30))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ConnectAsync(
                new NfcConnectOptions { AllowUserPrompts = false }, monitorCancellation.Token));
        }
        Assert.True(ports.Last.Closed);
        // Closing the USB stream does not by itself reset the tag. Preserve HALT across the next port open.
        ports.CreateChip = () => new Chip { LowVbat = true, Halted = true };
        using var deadline = new CancellationTokenSource(1000);
        await using var again = await provider.ConnectAsync(new NfcConnectOptions(), deadline.Token);
        Assert.Equal(Chip.Uid, again.Uid);
    }

    [Fact]
    public async Task Discovery_FallsBackWhenDifferentTagReplacesRememberedTag()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using (await provider.ConnectAsync(new NfcConnectOptions())) { }
        ports.Last.TagUid = [4, 9, 8, 7];
        ports.Last.Halted = false; // A newly placed, powered-up tag.
        ports.Last.Commands.Clear();
        using var deadline = new CancellationTokenSource(1000);
        await using var next = await provider.ConnectAsync(new NfcConnectOptions(), deadline.Token);
        Assert.Equal(new byte[] { 4, 9, 8, 7 }, next.Uid);
        Assert.Equal(2, ports.Last.Commands.Count(c => c.Command == 0x4A));
    }

    [Fact]
    public async Task WakeSelection_EncodesFourAndTenByteCascadeUids()
    {
        foreach (var uid in new[] { new byte[] { 4, 1, 2, 3 }, new byte[] { 4, 1, 2, 3, 4, 5, 6, 7, 8, 9 } })
        {
            var ports = new Ports { CreateChip = () => new Chip { TagUid = uid } };
            await using var provider = new Pn532NfcProvider(ports);
            await using (await provider.ConnectAsync(new NfcConnectOptions())) { }
            using var deadline = new CancellationTokenSource(1000);
            await using var again = await provider.ConnectAsync(new NfcConnectOptions(), deadline.Token);
            var parameters = ports.Last.Commands.Last(c => c.Command == 0x4A).Data;
            var expected = uid.Length == 4 ? uid : new byte[] { 0x88, 4, 1, 2, 0x88, 3, 4, 5, 6, 7, 8, 9 };
            Assert.Equal(expected, parameters[2..]);
        }
    }

    [Fact]
    public async Task ReadAny_Ntag215_QueriesVersionOnceAndReadsWholeDump()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        var dump = (AmiiTag.Tags.TagDump)await AmiiboV3NfcOperations.ReadAnyAsync(tag);
        Assert.Equal(ports.Last.Memory, dump.ToArray()[..540]);
        Assert.Equal(1, ports.Last.Commands.Count(c => c.Command == 0x42 && c.Data[0] == 0x60));
    }

    [Fact]
    public async Task ReadAny_Ntag215_RecoversFromRfProtocolError()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        var failed = false;
        ports.Last.RfHandler = command =>
        {
            if (command[0] != 0x3A || failed) return null;
            failed = true;
            return [0x0B];
        };
        var dump = (AmiiTag.Tags.TagDump)await AmiiboV3NfcOperations.ReadAnyAsync(tag);
        Assert.Equal(ports.Last.Memory, dump.ToArray()[..540]);
        Assert.Equal(1, ports.Last.Commands.Count(c => c.Command == 0x54));
    }

    [Fact]
    public async Task ReadAny_Ntag215_RestartsReadAfterCrcFailure()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        var failed = false;
        ports.Last.RfHandler = command =>
        {
            if (command[0] != 0x3A || failed) return null;
            failed = true;
            return [0, 1, 2, 3, 4]; // Valid UART frame, invalid RF CRC.
        };
        var dump = (AmiiTag.Tags.TagDump)await AmiiboV3NfcOperations.ReadAnyAsync(tag);
        Assert.Equal(ports.Last.Memory, dump.ToArray()[..540]);
        Assert.Equal(1, ports.Last.Commands.Count(c => c.Command == 0x54));
    }

    [Fact]
    public async Task Ntag215Read_StopsAfterThreeRfFailures()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfHandler = command => command[0] == 0x3A ? new byte[] { 0x0B } : null;
        await Assert.ThrowsAsync<NfcException>(() => AmiiboNfcOperations.ReadTagAsync(tag));
        Assert.Equal(3, ports.Last.Commands.Count(c => c.Command == 0x42 && c.Data[0] == 0x3A));
        Assert.Equal(2, ports.Last.Commands.Count(c => c.Command == 0x54));
    }

    [Fact]
    public async Task Ntag215Read_DoesNotRetryLostUartConnection()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfHandler = command => command[0] == 0x3A ? throw new IOException("USB adapter unplugged.") : null;
        await Assert.ThrowsAsync<NfcConnectionLostException>(() => AmiiboNfcOperations.ReadTagAsync(tag));
        Assert.Equal(1, ports.Last.Commands.Count(c => c.Command == 0x42 && c.Data[0] == 0x3A));
        Assert.Equal(0, ports.Last.Commands.Count(c => c.Command == 0x54));
    }

    [Fact]
    public async Task Ntag215Read_StopsIfReselectFails()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfHandler = command => command[0] == 0x3A ? new byte[] { 0x0B } : null;
        ports.Last.ReselectStatus = 0x27;
        await Assert.ThrowsAsync<NfcException>(() => AmiiboNfcOperations.ReadTagAsync(tag));
        Assert.Equal(1, ports.Last.Commands.Count(c => c.Command == 0x42 && c.Data[0] == 0x3A));
    }

    [Fact]
    public async Task Ntag215Read_CancellationStopsRecoveryDelay()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfHandler = command => command[0] == 0x3A ? new byte[] { 0x0B } : null;
        using var cancellation = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AmiiboNfcOperations.ReadTagAsync(tag, cancellationToken: cancellation.Token));
        Assert.Equal(0, ports.Last.Commands.Count(c => c.Command == 0x54));
    }

    [Fact]
    public async Task Write_RfFailureIsNotReplayedByDriver()
    {
        var ports = new Ports();
        await using var provider = new Pn532NfcProvider(ports);
        await using var tag = await provider.ConnectAsync(new NfcConnectOptions());
        ports.Last.RfHandler = command => command[0] == 0xA2 ? new byte[] { 0x0B } : null;
        await Assert.ThrowsAsync<NfcException>(() => tag.TransceiveAsync([0xA2, 4, 1, 2, 3, 4]));
        Assert.Equal(1, ports.Last.Commands.Count(c => c.Command == 0x42 && c.Data[0] == 0xA2));
    }

    private sealed class Ports : ISerialPortFactory, ISerialPortRequester
    {
        public bool IsSupported => true;
        public bool Granted = true;
        public int Requests;
        public int LastRate;
        public Chip Last = null!;
        public Func<Chip> CreateChip = () => new Chip { LowVbat = true };
        public Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NfcReaderInfo>>(Granted ? [new("COM7", "Test UART")] : []);
        public Task<NfcReaderInfo?> RequestPortAsync(CancellationToken cancellationToken = default)
        {
            ++Requests;
            Granted = true;
            return Task.FromResult<NfcReaderInfo?>(new("COM7", "Test UART"));
        }
        public Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default)
        {
            Assert.Equal("COM7", portId);
            LastRate = baudRate;
            Last = CreateChip();
            return Task.FromResult<ISerialPortStream>(Last);
        }
    }

    // This model decodes and checks the wire protocol independently, returning one byte per serial read to exercise
    // fragmentation. RF memory and ACKs model a small NTAG without depending on physical USB hardware.
    private sealed class Chip : ISerialPortStream
    {
        public static readonly byte[] Uid = [4, 1, 2, 3, 4, 5, 6];
        private readonly Channel<byte> _incoming = Channel.CreateUnbounded<byte>();
        public List<byte[]> Writes = [];
        public List<(byte Command, byte[] Data)> Commands = [];
        public List<byte[]> AirFrames = [];
        public byte TxMode = 0x80;
        public byte RxMode = 0x80;
        public byte BitFraming;
        public byte[] Memory = Enumerable.Range(0, 540).Select(i => (byte)i).ToArray();
        public byte[] Firmware = [0x32, 1, 6, 7];
        public byte[] TagUid = Uid.ToArray();
        public bool Halted;
        public byte[]? RfResponse;
        public Func<byte[], byte[]?>? RfHandler;
        public byte ReselectStatus;
        public byte[]? ResponseOverride;
        public byte? ResponseCommand;
        public byte[] Ack = [0, 0, 0xFF, 0, 0xFF, 0];
        public bool Closed;
        public bool CorruptChecksum;
        public bool Silent;
        public bool LowVbat;
        public bool AckOnly;
        public int EmptyPolls;
        public Func<byte, byte[], byte[]>? Handler;

        public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Closed) throw new IOException("Closed test port.");
            Writes.Add(data.ToArray());
            if (data.SequenceEqual(new byte[] { 0x55 }) || data.SequenceEqual(new byte[] { 0, 0, 0xFF, 0, 0xFF, 0 }))
                return Task.CompletedTask;
            var wakePreamble = data.Length >= 16 && data[0] == 0x55 && data[1] == 0x55;
            if (wakePreamble)
            {
                Assert.Equal(new byte[14], data[2..16]);
                data = data[16..];
            }
            Assert.Equal(new byte[] { 0, 0, 0xFF }, data[..3]);
            var extended = data[3] == 0xFF;
            var length = extended ? (data[5] << 8) | data[6] : data[3];
            var start = extended ? 8 : 5;
            Assert.Equal(0, extended ? (data[5] + data[6] + data[7]) & 255 : (data[3] + data[4]) & 255);
            Assert.Equal(length + start + 2, data.Length);
            Assert.Equal(0, data.Skip(start).Take(length + 1).Sum(b => (int)b) & 255);
            Assert.Equal(0xD4, data[start]);
            var command = data[start + 1];
            var parameters = data[(start + 2)..^2];
            Commands.Add((command, parameters));
            if (LowVbat)
            {
                // AN10609_2 2.4.2: normal-mode SAM must carry the HSU startup preamble. Wake/abort followed by
                // a later unprefixed SAM does not satisfy this startup contract.
                if (!wakePreamble || command != 0x14 || parameters[0] != 1) return Task.CompletedTask;
                LowVbat = false;
            }
            if (Silent) return Task.CompletedTask;
            foreach (var value in Ack) _incoming.Writer.TryWrite(value);
            if (AckOnly) return Task.CompletedTask;
            var result = Handler?.Invoke(command, parameters) ?? Respond(command, parameters);
            var body = new byte[] { 0xD5, ResponseCommand ?? (byte)(command + 1) }.Concat(result).ToArray();
            var checksum = unchecked((byte)-body.Sum(b => (int)b));
            if (CorruptChecksum) checksum ^= 1;
            byte[] header = body.Length < 256
                ? [0, 0, 0xFF, (byte)body.Length, unchecked((byte)-body.Length)]
                : [0, 0, 0xFF, 0xFF, 0xFF, (byte)(body.Length >> 8), (byte)body.Length,
                    unchecked((byte)-((body.Length >> 8) + (body.Length & 255)))];
            byte[] response = ResponseOverride ?? [0x78, 0x90, .. header, .. body, checksum, 0];
            foreach (var value in response) _incoming.Writer.TryWrite(value);
            return Task.CompletedTask;
        }

        private byte[] Respond(byte command, byte[] parameters)
        {
            switch (command)
            {
                case 0x02: return Firmware;
                case 0x08:
                    for (var offset = 0; offset < parameters.Length; offset += 3)
                    {
                        var address = (parameters[offset] << 8) | parameters[offset + 1];
                        var value = parameters[offset + 2];
                        if (address == 0x6302) TxMode = value;
                        if (address == 0x6303) RxMode = value;
                        if (address == 0x633D) BitFraming = value;
                    }
                    return [];
                case 0x4A:
                    if (EmptyPolls-- > 0) return [0];
                    if (parameters.Length > 2)
                    {
                        // Independently model PN532's directed WUPA selection with cascade-tag UID format.
                        var expected = TagUid.ToList();
                        if (TagUid.Length >= 7) expected.Insert(0, 0x88);
                        if (TagUid.Length == 10) expected.Insert(4, 0x88);
                        if (!parameters.Skip(2).SequenceEqual(expected)) return [0];
                        Halted = false;
                    }
                    else if (Halted) return [0]; // REQA cannot wake a halted tag.
                    return [1, 1, 0x44, 0, 0, (byte)TagUid.Length, .. TagUid];
                case 0x44:
                    var halt = (TxMode & 0x80) != 0 ? Iso14443Crc.Append(new byte[] { 0x50, 0x00 }) : new byte[] { 0x50, 0x00 };
                    AirFrames.Add(halt);
                    Halted = Iso14443Crc.Check(halt);
                    return [ReselectStatus];
                case 0x54:
                    // Native reactivation requires firmware receive CRC mode and a byte-aligned frame setup.
                    Assert.Equal(0x80, TxMode);
                    Assert.Equal(0x80, RxMode);
                    Assert.Equal(0, BitFraming);
                    if (ReselectStatus == 0) Halted = false;
                    return [ReselectStatus];
                case 0x06: return [4];
                case 0x42:
                    var air = (TxMode & 0x80) != 0 ? Iso14443Crc.Append(parameters) : parameters;
                    AirFrames.Add(air);
                    Assert.True(Iso14443Crc.Check(air));
                    Assert.Equal(0x80, TxMode);
                    Assert.Equal(0, RxMode);
                    if (Halted) return [1];
                    if (RfHandler?.Invoke(parameters) is byte[] injected) return injected;
                    if (RfResponse != null) return RfResponse;
                    switch (parameters[0])
                    {
                        case 0x60: return [0, .. Iso14443Crc.Append(new byte[] { 0, 4, 4, 2, 1, 0, 0x11, 3 })];
                        case 0x3C: return [0, .. Iso14443Crc.Append(new byte[32])];
                        case 0x30: return [0, .. Iso14443Crc.Append(Memory.AsSpan(parameters[1] * 4, 16))];
                        case 0x3A: return [0, .. Iso14443Crc.Append(Memory.AsSpan(parameters[1] * 4, (parameters[2] - parameters[1] + 1) * 4))];
                        case 0xA2:
                            parameters.AsSpan(2, 4).CopyTo(Memory.AsSpan(parameters[1] * 4, 4));
                            return [0, 0x0A];
                        default: return [1];
                    }
                default: return [];
            }
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            buffer[offset] = await _incoming.Reader.ReadAsync(cancellationToken);
            return 1;
        }
        public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
    }
}
