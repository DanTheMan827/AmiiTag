using AmiiTag.Nfc;
using AmiiTag.Nfc.Flipper;
using AmiiTag.Nfc.Mfrc522;
using AmiiTag.Nfc.Pn532;
using AmiiTag.NfcRelay;

// amiitag-nfc-relay [COM port] [--baud <rate>] [--timeout <ms>] [--log <file>]
//
// The tag on a serial NFC reader (MFRC522 or PN532, detected the same way as the app) is emulated by a Flipper Zero
// running the NFC Bridge app: the Flipper does anticollision with the real tag's UID, every frame its reader sends is
// passed to the real tag, and the real tag's answer goes back. Every exchange is logged. The Flipper only presents a
// tag while one is on the serial reader: when it's taken off, the emulation stops and the reader polls for the next.

string? portArgument = null;
string? logPath = null;
var answerTimeout = TimeSpan.FromMilliseconds(250);
var baudRate = Pn532NfcProvider.FastBaudRate;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--timeout" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ms) && ms > 0:
            answerTimeout = TimeSpan.FromMilliseconds(ms);
            i++;
            break;
        case "--baud" when i + 1 < args.Length && int.TryParse(args[i + 1], out var rate) && Pn532NfcProvider.SettingsBaudRates.Contains(rate):
            baudRate = rate;
            i++;
            break;
        case "--log" when i + 1 < args.Length:
            logPath = args[++i];
            break;
        case "-h" or "--help" or "/?":
            Console.WriteLine("Usage: amiitag-nfc-relay [COM port] [--baud <rate>] [--timeout <ms>] [--log <file>]");
            Console.WriteLine();
            Console.WriteLine("  COM port   the serial NFC reader's port (default: the first one that isn't a Flipper)");
            Console.WriteLine($"  --baud     the reader's serial speed once found: {string.Join(", ", Pn532NfcProvider.SettingsBaudRates)} (default {Pn532NfcProvider.FastBaudRate})");
            Console.WriteLine("  --timeout  how long the Flipper waits for the real tag's answer (default 250 ms)");
            Console.WriteLine("  --log      also write the traffic to this file");
            return 0;
        case var value when !value.StartsWith('-') && portArgument == null:
            portArgument = value;
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]} (see --help)");
            return 2;
    }
}

using var log = new RelayLog(logPath);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var ports = new SerialPortFactory();
var pn532 = new Pn532NfcProvider(ports);
var reader = new AutoDetectSerialNfcProvider(new Mfrc522NfcProvider(ports), pn532) { BaudRate = baudRate };
FlipperBridge? bridge = null;
TagRelay? relay = null;
var listening = false;
try
{
    var portId = portArgument ?? (await ports.GetPortsAsync(stop.Token)).FirstOrDefault()?.Id;
    if (portId == null)
    {
        log.Error("No serial ports found. Plug in the NFC reader, or name its port.");
        return 1;
    }

    var transport = await HidBridgeTransport.OpenAsync(stop.Token);
    if (transport == null)
    {
        log.Error("No Flipper Zero running the NFC Bridge app was found over USB.");
        return 1;
    }

    // Until a tag is relayed, anything that reaches the Flipper gets silence.
    bridge = new FlipperBridge(transport) { Responder = _ => BridgeAnswer.Silence };
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    bridge.FrameHandled += log.Exchange;
    bridge.DeviceLog += text => log.Write(EmulationLogKind.Device, text);
    bridge.TagEvent += (type, micros) =>
    {
        log.TagEvent(type, micros);
        if (type is BridgeMessageType.FieldOff or BridgeMessageType.Halted)
        {
            // The emulated tag lost its state (power, or HLTA): put the real one back in step before the next frame.
            relay?.ResetRealTag();
        }
    };
    bridge.Closed += error =>
    {
        if (error != null)
        {
            log.Error($"Connection to the Flipper lost: {error.Message}");
        }

        closed.TrySetResult();
    };
    bridge.Start();

    var hello = await bridge.HelloAsync(stop.Token);
    if (!hello.Capabilities.HasFlag(BridgeCapabilities.ListenA))
    {
        log.Error("This Flipper firmware can't emulate ISO 14443-A cards.");
        return 1;
    }

    await bridge.TableClearAsync(stop.Token);
    log.Info($"{hello.Firmware} (protocol {hello.ProtocolVersion}, over {transport.Name})");

    while (!stop.IsCancellationRequested && !closed.Task.IsCompleted)
    {
        log.Info($"Serial reader on {portId}: waiting for a tag (Ctrl+C to quit)...");
        var tag = await reader.ConnectAsync(new NfcConnectOptions { ReaderId = portId, AllowUserPrompts = false }, stop.Token);
        var detected = reader.DetectedOn(portId);
        var speed = ReferenceEquals(detected, pn532) ? pn532.LinkBaudRate : reader.BaudRate;
        log.Info($"{detected?.DisplayName ?? "Reader"} at {speed:N0} baud: tag {Convert.ToHexString(tag.Uid)}");
        if (tag.Uid.Length is not (4 or 7 or 10))
        {
            log.Error($"The reader reported a {tag.Uid.Length} byte UID; the Flipper can only emulate 4, 7 or 10 byte UIDs.");
            await CloseQuietlyAsync(tag);
            await Task.Delay(1000, stop.Token);
            continue;
        }

        // GET_VERSION and READ_SIG never change for a tag, and readers can be impatient with them: an iPhone sends
        // GET_VERSION while it's still discovering the tag, and gives up on a tag that takes a relay's round trip to
        // answer. Preloaded into the Flipper, they're answered at once (shown as "answer table" in the log).
        await bridge.TableClearAsync(stop.Token);
        foreach (var command in new byte[][] { [0x60], [0x3C, 0x00] })
        {
            if (await TryTransceiveAsync(tag, command, answerTimeout, stop.Token) is { Length: > 1 } answer)
            {
                await bridge.TableSetAsync(command, BridgeAnswer.Frame(answer), stop.Token);
                log.Info($"Preloaded {NfcCommandNames.Describe(command)}: {Convert.ToHexString(answer)}");
            }
        }

        relay = new TagRelay(tag, answerTimeout, log);
        bridge.Responder = relay.Respond;

        // NTAG21x / N2 Elite: ATQA 44 00 (air order), SAK 00. The serial readers don't report them.
        await bridge.ListenIso14443AAsync(tag.Uid, [0x44, 0x00], 0x00, BridgeListenFlags.Default, answerTimeout, stop.Token);
        listening = true;
        log.Info($"Relaying: the Flipper presents UID {Convert.ToHexString(tag.Uid)}, waiting up to {answerTimeout.TotalMilliseconds:F0} ms for each answer.");
        log.Header();

        // Follow the serial reader: while frames flow they show the tag is there; when quiet, check it now and then.
        while (!relay.Lost.IsCompleted && !closed.Task.IsCompleted)
        {
            await Task.WhenAny(relay.Lost, closed.Task, Task.Delay(250, stop.Token));
            stop.Token.ThrowIfCancellationRequested();
            await relay.CheckPresenceAsync(stop.Token);
        }

        if (closed.Task.IsCompleted)
        {
            break;
        }

        log.Info($"Tag removed from the serial reader ({relay.Lost.Result}): the Flipper stops presenting it.");
        bridge.Responder = _ => BridgeAnswer.Silence;
        await bridge.StopAsync(stop.Token);
        listening = false;
        relay = null;
        await CloseQuietlyAsync(tag);
    }

    return closed.Task.IsCompleted && !stop.IsCancellationRequested ? 1 : 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    return 0;
}
catch (Exception ex)
{
    log.Error(ex.Message);
    return 1;
}
finally
{
    if (bridge != null)
    {
        if (listening)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await bridge.StopAsync(timeout.Token);
            }
            catch
            {
            }
        }

        await bridge.DisposeAsync();
    }

    if (relay != null)
    {
        await CloseQuietlyAsync(relay.Tag);
    }

    // Field off and the reader back at its power-on speed (a PN532 or MFRC522 keeps a switched one until power off).
    var wasSwitched = pn532.LinkBaudRate != Pn532NfcProvider.PowerOnBaudRate;
    await reader.ReleaseAsync();
    if (wasSwitched)
    {
        log.Info(pn532.LinkBaudRate == Pn532NfcProvider.PowerOnBaudRate
            ? $"PN532 back at {Pn532NfcProvider.PowerOnBaudRate:N0} baud."
            : $"Couldn't put the PN532 back at {Pn532NfcProvider.PowerOnBaudRate:N0} baud; unplug it to reset it.");
    }

    log.Info("Stopped.");
}

// The tag's answer, or null for a NAK, no answer or an error (the command is then just relayed like the rest).
static async Task<byte[]?> TryTransceiveAsync(INfcTagConnection tag, byte[] command, TimeSpan timeout, CancellationToken cancellationToken)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(timeout);
    try
    {
        return await tag.TransceiveAsync(command, deadline.Token);
    }
    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
    {
        // A NAK leaves the tag idle: wake it before the relay starts.
        if (ex is not OperationCanceledException && tag is INfcReselectable { CanReselect: true } reselectable)
        {
            try
            {
                await reselectable.ReselectAsync(cancellationToken);
            }
            catch
            {
            }
        }

        return null;
    }
}

static async Task CloseQuietlyAsync(INfcTagConnection tag)
{
    try
    {
        await tag.CloseAsync();
    }
    catch
    {
    }
}
