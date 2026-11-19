using AmiiTag.Tags;

namespace AmiiTag.Nfc.PcSc;

/// <summary>
/// How raw NFC-A commands are tunnelled through a particular PC/SC reader.
/// Add a new implementation (and register it with <see cref="PcscStrategySelector"/>) to support another reader family.
/// </summary>
public interface IPcscTransceiveStrategy
{
    string Name { get; }

    /// <summary>
    /// True if raw NFC-A commands are passed straight to the tag (direct command mode). Strategies that aren't
    /// direct can only read.
    /// </summary>
    bool IsDirect { get; }

    int MaxFastReadPages { get; }

    /// <summary>
    /// Prepares the reader for this strategy. Return false if the reader doesn't support it.
    /// </summary>
    bool TryBegin(PcscCard card);

    byte[] Transceive(PcscCard card, byte[] command);

    byte[] Transceive(PcscCard card, byte[] command, TimeSpan responseTimeout) => Transceive(card, command);

    void End(PcscCard card);
}

/// <summary>Raw tag activation supported by a reader, without a PC/SC card reset or RF-off command.</summary>
public interface IPcscReselectStrategy
{
    void Reselect(PcscCard card, byte[] expectedUid);
}

/// <summary>
/// Picks a strategy for a reader. Direct command strategies are always preferred: ones whose name filter matches the
/// reader first, then the generic ones. Each direct strategy has to prove it really reaches the tag (a raw READ of
/// page 0) before it's used. Only when no direct mode works does the read-only storage card fallback get a turn.
/// </summary>
public sealed class PcscStrategySelector
{
    private readonly List<(Func<string, bool> Matches, Func<IPcscTransceiveStrategy> Create)> _specific = [];
    private readonly List<Func<IPcscTransceiveStrategy>> _generic = [];

    // The strategy that last worked on each reader, tried first next time: probing the others means a failed attempt
    // and a tag reset (an extra beep on ACR readers) on every connect.
    private readonly Dictionary<string, string> _lastWorking = new(StringComparer.OrdinalIgnoreCase);

    public PcscStrategySelector()
    {
        // ACR122U: PN532 behind the reader, reached with direct transmit pseudo APDUs.
        Register(name => name.Contains("ACR122", StringComparison.OrdinalIgnoreCase), () => new Pn53xDirectTransmitStrategy());

        // ACR1552U and most modern readers: PC/SC 2.01 part 3 transparent exchange. Some readers only answer when the
        // exchange carries an explicit timeout, so that variant is tried second.
        RegisterGeneric(() => new TransparentSessionStrategy());
        RegisterGeneric(() => new TransparentSessionStrategy(withTimer: true));

        // Last resort when no direct mode works: storage card READ BINARY. Reading only.
        RegisterGeneric(() => new StorageCardStrategy());
    }

    /// <summary>
    /// A raw READ of page 0, which every NTAG answers, to confirm commands really reach the tag.
    /// </summary>
    private static bool ReachesTag(IPcscTransceiveStrategy strategy, PcscCard card)
    {
        try
        {
            return strategy.Transceive(card, [Ntag215Client.CmdRead, 0x00]).Length >= 16;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReachesTagWithRecovery(IPcscTransceiveStrategy strategy, PcscCard card)
    {
        // A v3 figure can be selected by PC/SC while its I²C side still owns memory. Repeated card resets
        // restart that startup window. Keep the established reader mode/field while waiting and waking it.
        byte[]? expectedUid = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (ReachesTag(strategy, card)) return true;
            if (card.LastTransportError != null) return false;
            if (attempt == 4) break;
            card.Log?.Add($"(tag not ready; waiting {100 * (attempt + 1)} ms without card reset)");
            Thread.Sleep(100 * (attempt + 1));
            if (strategy is IPcscReselectStrategy reselectable)
            {
                try
                {
                    expectedUid ??= card.GetUid();
                    if (card.LastTransportError != null) return false;
                    reselectable.Reselect(card, expectedUid);
                }
                catch (NfcConnectionLostException) { throw; }
                catch (NfcException ex)
                {
                    card.Log?.Add($"(tag reselect failed: {ex.Message})");
                    return false;
                }
                catch (PCSC.Exceptions.PCSCException)
                {
                    return false;
                }
            }
        }
        return false;
    }

    public void Register(Func<string, bool> readerNameMatches, Func<IPcscTransceiveStrategy> create) =>
        _specific.Add((readerNameMatches, create));

    public void RegisterGeneric(Func<IPcscTransceiveStrategy> create) => _generic.Add(create);

    public IPcscTransceiveStrategy Select(string readerName, PcscCard card)
    {
        var candidates = _specific.Where(s => s.Matches(readerName)).Select(s => s.Create).Concat(_generic)
            .Select(create => create())
            .OrderBy(strategy => strategy.IsDirect ? 0 : 1)
            .ToList();

        string? known;
        lock (_lastWorking)
        {
            _lastWorking.TryGetValue(readerName, out known);
        }

        if (known != null)
        {
            candidates = candidates.OrderBy(strategy => strategy.Name == known ? 0 : 1).ToList();
        }

        // Every APDU and reply is recorded, so a reader that refuses everything can be diagnosed from the error.
        var log = new List<string>();
        card.Log = log;
        try
        {
            foreach (var strategy in candidates)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    card.LastTransportError = null;
                    log.Add($"-- {strategy.Name} (attempt {attempt + 1})");
                    var begun = strategy.TryBegin(card);
                    if (begun && (!strategy.IsDirect || ReachesTagWithRecovery(strategy, card)))
                    {
                        lock (_lastWorking)
                        {
                            _lastWorking[readerName] = strategy.Name;
                        }
                        return strategy;
                    }

                    // A removed/reset PC/SC handle is not evidence that this protocol is unsupported.
                    // Recover with Leave (no requested RF reset), then retry THIS strategy first.
                    if (card.LastTransportError is { } transportError)
                    {
                        if (attempt < 2 && card.TryReconnectPreservingField())
                        {
                            continue;
                        }
                        throw new NfcConnectionLostException(
                            $"Lost the connection to {readerName} while probing {strategy.Name}.\n{string.Join("\n", log.TakeLast(24))}", transportError);
                    }

                    if (begun)
                    {
                        try { strategy.End(card); } catch { }
                    }
                    card.ResetCard();
                    break;
                }
            }
        }
        finally
        {
            card.Log = null;
        }

        System.Diagnostics.Debug.WriteLine($"PC/SC strategy selection for {readerName} failed:\n{string.Join("\n", log)}");
        throw new NfcException(
            $"Don't know how to talk to tags through \"{readerName}\". What the reader answered:\n{string.Join("\n", log.TakeLast(24))}");
    }
}

/// <summary>
/// ACR122U: PN532 InCommunicateThru (D4 42) wrapped in FF 00 00 00 direct transmit pseudo APDUs, for every command.
/// </summary>
/// <remarks>
/// InDataExchange can't be used: it only works on a target registered with the PN532's own InListPassiveTarget, which
/// the reader's automatic polling doesn't do (the PN532 answers 0x27, "not acceptable in the current context").
/// InCommunicateThru sends raw frames, but with the PN532's hardware CRC on it can't return a write's 4 bit ACK, which
/// has no CRC. So the CRC is switched off in the CIU (TxMode/RxMode) for the session and added and checked here, as
/// nfcpy's PN53x driver does for Type 2 tags. It's switched back on when the session ends.
/// </remarks>
public sealed class Pn53xDirectTransmitStrategy : IPcscTransceiveStrategy, IPcscReselectStrategy
{
    private const ushort CiuTxMode = 0x6302;
    private const ushort CiuRxMode = 0x6303;
    private const byte CrcOff = 0x00; // 106 kbit/s, no CRC.
    private const byte CrcOn = 0x80;  // 106 kbit/s, CRC enabled (the reader's normal setting).

    private bool _crcChanged;
    private byte? _timeoutCode;
    private bool _selectionRetriesChanged;

    public string Name => "PN53x direct transmit";

    public bool IsDirect => true;

    public int MaxFastReadPages => 32;

    public bool TryBegin(PcscCard card)
    {
        try
        {
            // Firmware version (FF 00 48 00 00): checks the reader understands the pseudo APDUs.
            if (card.Transmit([0xFF, 0x00, 0x48, 0x00, 0x00]).Length == 0)
            {
                return false;
            }

            // A PC/SC reconnect/reset may have restored PN532 defaults since the previous attempt.
            _timeoutCode = null;
            _crcChanged = SetCrc(card, CrcOff);
            return _crcChanged;
        }
        catch
        {
            return false;
        }
    }

    public byte[] Transceive(PcscCard card, byte[] command) =>
        Transceive(card, command, TimeSpan.FromMilliseconds(800));

    public byte[] Transceive(PcscCard card, byte[] command, TimeSpan responseTimeout)
    {
        // UM0701-02 7.3.1: fRetryTimeout = 100 us * 2^(n-1). Round up: 10 ms -> 12.8 ms,
        // 800 ms -> 819.2 ms. This changes response timing only, never the RF field or selected tag.
        var code = (byte)1;
        for (var micros = 100d; micros < responseTimeout.TotalMicroseconds && code < 16; micros *= 2)
        {
            code++;
        }
        try
        {
            try
            {
                SetResponseTimeout(card, code);
                // Reader helpers (e.g. FF CA UID) and reader-managed activation may restore hardware CRC.
                // Our Type 2 frames already include CRC_A, so reassert raw framing before EVERY exchange.
                if (!SetCrc(card, CrcOff))
                    throw new NfcConnectionLostException("Could not configure raw PN532 framing.", new InvalidOperationException());
                _crcChanged = true;
            }
            catch (PCSC.Exceptions.PCSCException ex)
            {
                // No tag packet was sent yet; this cannot be the sector command's passive ACK.
                throw new NfcConnectionLostException("Lost the reader while configuring its response timeout.", ex);
            }
            return Exchange(card, command);
        }
        finally
        {
            if (code != 0x0E && card.LastTransportError == null)
            {
                SetResponseTimeout(card, 0x0E);
            }
        }
    }

    private void SetResponseTimeout(PcscCard card, byte code)
    {
        if (_timeoutCode == code) return;
        _timeoutCode = null;
        Pn532(card, [0x32, 0x02, 0x00, 0x0B, code]);
        _timeoutCode = code;
    }

    public void Reselect(PcscCard card, byte[] expectedUid)
    {
        if (!SetCrc(card, CrcOn)) throw new NfcException("Could not prepare the PN532 for tag activation.");
        try
        {
            // Bound InListPassiveTarget: its default passive-activation retry count is infinite (UM0701-02).
            Pn532(card, [0x32, 0x05, 0xFF, 0x01, 0x00]);
            _selectionRetriesChanged = true;
            var response = Pn532(card, [0x4A, 0x01, 0x00]); // One ISO14443-A target, 106 kbit/s.
            if (response.Length < 6 || response[0] != 1 || response[5] is not (4 or 7 or 10) || response.Length < 6 + response[5])
                throw new NfcException("The PN532 could not activate the tag.");
            var uid = response.AsSpan(6, response[5]);
            if (expectedUid.Length != 0 && !uid.SequenceEqual(expectedUid))
                throw new NfcConnectionLostException("A different tag answered the PN532 activation.", new InvalidOperationException());
            card.Log?.Add("(tag reselected without PC/SC reset)");
        }
        finally
        {
            // Activation restores RF protocol registers, so reapply raw Type 2 framing before the next command.
            _timeoutCode = null;
            if (!SetCrc(card, CrcOff)) throw new NfcException("Could not restore raw PN532 framing after activation.");
            _crcChanged = true;
        }
    }

    private static byte[] Exchange(PcscCard card, byte[] command)
    {
        var frame = Iso14443Crc.Append(command);
        var response = Pn532(card, [0x42, .. frame]);

        var status = response[0] & 0x3F;
        if (status != 0)
        {
            throw new NfcException(status switch
            {
                0x01 => "The tag didn't respond.",
                0x02 => "The tag's answer had a CRC error.",
                0x27 => "The reader refused the command (PN532 error 27: not acceptable in the current context).",
                _ => $"Reader error 0x{status:X2}.",
            });
        }

        var data = response.AsSpan(1).ToArray();

        // A one byte answer is the 4 bit ACK/NAK (no CRC).
        if (data.Length == 1)
        {
            return [(byte)(data[0] & 0x0F)];
        }

        if (data.Length < 3 || !Iso14443Crc.Check(data))
        {
            throw new NfcException($"The tag's answer had a CRC error ({Hex.ToSpacedHex(data)}).");
        }

        return data.AsSpan(0, data.Length - 2).ToArray();
    }

    public void End(PcscCard card)
    {
        // Put the CRC and the documented default activation retries back for the reader's own card handling.
        try
        {
            if (_selectionRetriesChanged)
            {
                Pn532(card, [0x32, 0x05, 0xFF, 0x01, 0xFF]);
                _selectionRetriesChanged = false;
            }
        }
        finally
        {
            if (_crcChanged)
            {
                SetCrc(card, CrcOn);
                _crcChanged = false;
            }
        }
    }

    /// <summary>
    /// WriteRegister (D4 08) for CIU_TxMode and CIU_RxMode.
    /// </summary>
    private static bool SetCrc(PcscCard card, byte mode)
    {
        try
        {
            Pn532(card, [0x08, CiuTxMode >> 8, CiuTxMode & 0xFF, mode, CiuRxMode >> 8, CiuRxMode & 0xFF, mode]);
            return true;
        }
        catch (NfcException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends a PN532 command (without the D4 prefix) and returns its answer after the D5 xx header.
    /// </summary>
    private static byte[] Pn532(PcscCard card, byte[] command)
    {
        byte[] apdu = [0xFF, 0x00, 0x00, 0x00, (byte)(command.Length + 1), 0xD4, .. command];
        var response = card.TransmitExpectOk(apdu);
        if (response.Length < 2 || response[0] != 0xD5 || response[1] != (byte)(command[0] + 1))
        {
            throw new NfcException($"Unexpected reader response {Hex.ToSpacedHex(response)}.");
        }

        return response.AsSpan(2).ToArray();
    }
}

/// <summary>
/// PC/SC 2.01 part 3 transparent session (FF C2). Used by the ACR1552U and many other readers.
/// </summary>
public sealed class TransparentSessionStrategy : IPcscTransceiveStrategy
{
    /// <summary>
    /// Timer data object (5F46): the response timeout in microseconds, least significant byte first. 100 ms.
    /// </summary>
    private static readonly byte[] TimerObject = [0x5F, 0x46, 0x04, 0xA0, 0x86, 0x01, 0x00];

    private readonly bool _withTimer;

    public TransparentSessionStrategy(bool withTimer = false)
    {
        _withTimer = withTimer;
    }

    public string Name => _withTimer ? "PC/SC transparent session (with timer)" : "PC/SC transparent session";

    public bool IsDirect => true;

    public int MaxFastReadPages => 16;

    public bool TryBegin(PcscCard card)
    {
        try
        {
            // Start transparent session. Some readers want the APDU as case 4 (with Le), others as case 3.
            if (!PcscCard.IsOk(card.Transmit([0xFF, 0xC2, 0x00, 0x00, 0x02, 0x81, 0x00])) &&
                !PcscCard.IsOk(card.Transmit([0xFF, 0xC2, 0x00, 0x00, 0x02, 0x81, 0x00, 0x00])))
            {
                return false;
            }

            // Cycle the RF field (Turn Off RF 83, Turn On RF 84): some readers (ACR1552U) leave it off once a session has
            // started, so the tag never answered; it also wakes a tag an earlier attempt left halted. Not every reader
            // has these, so failures are ignored.
            TryTransmit(card, [0xFF, 0xC2, 0x00, 0x00, 0x02, 0x83, 0x00]);
            TryTransmit(card, [0xFF, 0xC2, 0x00, 0x00, 0x02, 0x84, 0x00]);

            // Ask for ISO 14443-A layer 3 (the reader activates the tag again) so frames go straight to the tag. Not all
            // readers need/allow this; the test READ that follows decides whether the session works.
            card.Transmit([0xFF, 0xC2, 0x00, 0x02, 0x04, 0x8F, 0x02, 0x00, 0x03]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryTransmit(PcscCard card, byte[] apdu)
    {
        try
        {
            card.Transmit(apdu);
        }
        catch (PCSC.Exceptions.PCSCException)
        {
        }
    }

    /// <summary>
    /// Transmission and Reception Flag objects (90h) tried for commands the tag answers with a 4 bit ACK, which has no
    /// CRC: none (the reader's default), then "don't check/remove the received CRC" in both byte orders, since readers
    /// read the 16 bit field differently. The first one this reader accepts is remembered.
    /// </summary>
    private static readonly byte[]?[] AckFlagVariants = [null, [0x90, 0x02, 0x00, 0x02], [0x90, 0x02, 0x02, 0x00]];

    private int _ackVariant = -1;
    private bool _compatibilityDataNext;

    public byte[] Transceive(PcscCard card, byte[] command) => TransceiveCore(card, command, null);

    public byte[] Transceive(PcscCard card, byte[] command, TimeSpan responseTimeout) =>
        TransceiveCore(card, command, responseTimeout);

    private byte[] TransceiveCore(PcscCard card, byte[] command, TimeSpan? responseTimeout)
    {
        if (!ExpectsAck(command))
        {
            return Exchange(card, command, null, responseTimeout);
        }

        if (_ackVariant >= 0)
        {
            return Exchange(card, command, AckFlagVariants[_ackVariant], responseTimeout);
        }

        // Rewriting a page (or reselecting a sector) is harmless, so trying again with other flags is safe.
        var errors = new List<string>();
        for (var i = 0; i < AckFlagVariants.Length; i++)
        {
            try
            {
                var response = Exchange(card, command, AckFlagVariants[i], responseTimeout);
                _ackVariant = i;
                return response;
            }
            catch (NfcException ex) when (ex is not NfcCommandNotSupportedException)
            {
                errors.Add(ex.Message);
            }
        }

        throw new NfcException($"The reader couldn't pass the tag's acknowledgement back. {string.Join(" / ", errors)}");
    }

    private bool ExpectsAck(byte[] command)
    {
        if (_compatibilityDataNext)
        {
            _compatibilityDataNext = false;
            return command.Length == 16;
        }

        if (command.Length == 2 && command[0] == 0xA0)
        {
            _compatibilityDataNext = true;
            return true;
        }

        return (command.Length == 6 && command[0] == Ntag215Client.CmdWrite) ||
               (command.Length == 2 && command[0] == 0xC2 && command[1] == 0xFF) ||
               IsFastWrite(command);
    }

    /// <summary>
    /// NTAG I²C FAST_WRITE (A6, start, end, 64 bytes of SRAM), answered with a 4 bit ACK.
    /// </summary>
    private static bool IsFastWrite(byte[] command) => command.Length == 3 + 64 && command[0] == 0xA6;

    private byte[] Exchange(PcscCard card, byte[] command, byte[]? flags, TimeSpan? responseTimeout)
    {
        var dataObject = new List<byte>();
        if (responseTimeout.HasValue)
        {
            var micros = (uint)Math.Clamp(responseTimeout.Value.TotalMicroseconds, 1, uint.MaxValue);
            dataObject.AddRange([0x5F, 0x46, 0x04, (byte)micros, (byte)(micros >> 8), (byte)(micros >> 16), (byte)(micros >> 24)]);
        }
        else if (_withTimer)
        {
            dataObject.AddRange(TimerObject);
        }

        if (flags != null)
        {
            dataObject.AddRange(flags);
        }

        dataObject.Add(0x95);
        dataObject.Add((byte)command.Length);
        dataObject.AddRange(command);

        byte[] apdu = [0xFF, 0xC2, 0x00, 0x01, (byte)dataObject.Count, .. dataObject, 0x00];
        var response = card.Transmit(apdu);
        if (!PcscCard.IsOk(response))
        {
            throw new NfcException($"Reader returned {Describe(response)} for {Hex.ToSpacedHex(apdu)}.");
        }

        byte[]? data = null;
        var validBits = 0;
        var offset = 0;
        while (offset + 2 <= response.Length - 2)
        {
            var tag = response[offset];
            var length = response[offset + 1];
            var valueOffset = offset + 2;
            if (valueOffset + length > response.Length - 2)
            {
                break;
            }

            var value = response.AsSpan(valueOffset, length);
            switch (tag)
            {
                case 0xC0 when length >= 3:
                    // Generic error status: xx 90 00 is success, 64 01 means no answer from the tag.
                    if (!(value[1] == 0x90 && value[2] == 0x00))
                    {
                        throw new NfcException(value[1] == 0x64 && value[2] == 0x01
                            ? "The tag didn't respond."
                            : $"Reader error {Hex.ToSpacedHex(value)} (sent {Hex.ToSpacedHex(apdu)}, got {Hex.ToSpacedHex(response)}).");
                    }

                    break;
                case 0x92 when length >= 1:
                    validBits = value[0];
                    break;
                case 0x97:
                    data = value.ToArray();
                    break;
            }

            offset = valueOffset + length;
        }

        if (data == null || data.Length == 0)
        {
            // A 4 bit ACK that the reader reports as "no data".
            return ExpectsAckWithoutConsuming(command) ? new byte[] { 0x0A } : Array.Empty<byte>();
        }

        if (validBits is > 0 and < 8 && data.Length == 1)
        {
            data[0] &= 0x0F;
        }

        return data;
    }

    private static bool ExpectsAckWithoutConsuming(byte[] command) =>
        (command.Length == 6 && command[0] == Ntag215Client.CmdWrite) ||
        (command.Length == 2 && command[0] is 0xA0 or 0xC2) ||
        command.Length == 16 ||
        IsFastWrite(command);

    private static string Describe(byte[] response) =>
        response.Length >= 2 ? $"status {response[^2]:X2}{response[^1]:X2}" : "no status";

    public void End(PcscCard card)
    {
        card.Transmit([0xFF, 0xC2, 0x00, 0x00, 0x02, 0x82, 0x00]);
    }
}

/// <summary>
/// Not direct command mode: the reader's own storage card READ BINARY. Enough to read a tag on almost any reader,
/// but GET_VERSION, READ_SIG, FAST_READ, PWD_AUTH, SECTOR_SELECT and WRITE aren't possible, so it's read only.
/// </summary>
public sealed class StorageCardStrategy : IPcscTransceiveStrategy
{
    public string Name => "Storage card commands (read only)";

    public bool IsDirect => false;

    public int MaxFastReadPages => 1;

    public bool TryBegin(PcscCard card)
    {
        try
        {
            return PcscCard.IsOk(card.Transmit([0xFF, 0xB0, 0x00, 0x00, 0x10]));
        }
        catch
        {
            return false;
        }
    }

    public byte[] Transceive(PcscCard card, byte[] command)
    {
        if (command.Length == 2 && command[0] == Ntag215Client.CmdRead)
        {
            return card.TransmitExpectOk([0xFF, 0xB0, 0x00, command[1], 0x10]);
        }

        throw new NfcCommandNotSupportedException(command.Length > 0 ? command[0] : (byte)0);
    }

    public void End(PcscCard card)
    {
    }
}
