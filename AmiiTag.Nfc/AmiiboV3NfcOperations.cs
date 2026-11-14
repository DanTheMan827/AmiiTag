using AmiiTag.Tags;

namespace AmiiTag.Nfc;

/// <summary>
/// Reading "v3" amiibo (NTAG I²C Plus 2K) and choosing the right reader for whatever tag is presented.
/// </summary>
public static class AmiiboV3NfcOperations
{
    public const byte CmdSectorSelect = 0xC2;

    // NC_REG (session page EC, byte 0).
    public const byte NcPassThroughOn = 0x40;
    public const byte NcSramMirrorOn = 0x02;

    // NS_REG (session page ED, byte 2).
    public const byte NsSramI2cReady = 0x08;

    // PT_I2C (page E7, byte 0): sector 1 is password protected.
    public const byte PtI2c2kProt = 0x08;

    /// <summary>
    /// How long to wait for the figure's I²C side to put the lock-on response in SRAM.
    /// </summary>
    public static readonly TimeSpan DefaultLockOnTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads an original (NTAG215) or v3 (NTAG I²C Plus 2K) amiibo, picking by GET_VERSION. Readers that aren't in
    /// direct command mode can't send GET_VERSION, so the amiibo ID's last byte (02 or 03, at the same address on
    /// both tags) decides instead.
    /// </summary>
    public static async Task<IAmiiboImage> ReadAnyAsync(INfcTagConnection connection, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = new Ntag215Client(connection);
        var version = await WithRecoveryAsync(connection, () => client.GetVersionAsync(cancellationToken), null, cancellationToken);
        if (version != null ? version.IsNtagI2cPlus2k : await LooksLikeV3Async(connection, cancellationToken))
        {
            return await ReadV3Async(connection, progress, cancellationToken: cancellationToken);
        }

        return await AmiiboNfcOperations.ReadTagAsync(connection, progress, cancellationToken);
    }

    private static async Task<bool> LooksLikeV3Async(INfcTagConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            // Pages 15h-18h: the ID is raw 0x054..0x05B on both NTAG215 and v3 images.
            var pages = await connection.TransceiveAsync([Ntag215Client.CmdRead, 0x15], cancellationToken);
            return pages.Length >= 8 && pages[7] == 0x03;
        }
        catch (NfcException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads everything an NTAG I²C Plus 2K exposes over NFC into AmiiboZero's 2048 byte layout: sector 0 (user memory,
    /// lock, configuration and session registers), all of sector 1, and the lock-on response from SRAM, which is stored
    /// in the image at sector 0 pages F0-FF. Only the invalid pages (EA-EB, EE-EF) and PWD/PACK (which always read as
    /// zero) aren't real data.
    /// </summary>
    /// <remarks>
    /// Sector 1, the session registers and SRAM need direct command mode (SECTOR_SELECT, FAST_READ). Without it,
    /// sector 0 is still read with plain READs; that covers all of the amiibo data, but sector 1 stays blank and the
    /// lock-on can usually not be read.
    /// </remarks>
    public static async Task<AmiiboV3Dump> ReadV3Async(
        INfcTagConnection connection,
        IProgress<double>? progress = null,
        TimeSpan? lockOnTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var client = new Ntag215Client(connection);
        var raw = new byte[AmiiboV3Dump.ImageLength];
        var sector0 = new Progress<double>(p => progress?.Report(p * 0.45));
        var sector1 = new Progress<double>(p => progress?.Report(0.45 + p * 0.45));

        connection.SetStatus("Reading tag...");

        // 00-E7: UID, lock, CC, user EEPROM, dynamic lock and AUTH0..PT_I2C. A READ fallback ends at E4-E7, before EA.
        (await ReadPagesRecoveringAsync(connection, client, 0x00, AmiiboV3Dump.PtI2cPage, 0, sector0, cancellationToken)).CopyTo(raw, 0);

        // E8-E9 configuration and EC-ED session registers.
        var config = await ReadWindowAsync(connection, AmiiboV3Dump.PtI2cPage + 1, AmiiboV3Dump.LastConfigPage, readStart: 0xE6, cancellationToken);
        config?.CopyTo(raw, (AmiiboV3Dump.PtI2cPage + 1) * 4);
        var session = await ReadSessionAsync(connection, cancellationToken);
        session?.CopyTo(raw, AmiiboV3Dump.SessionPage0 * 4);

        // Sector 1. Real figures set PT_I2C.2K_PROT (page E7 bit 3): sector 1 is password protected and its reads are
        // NAKed without PWD_AUTH (emulators like AmiiboZero don't enforce it). The amiibo password (derived from the UID,
        // as on NTAG215 amiibo) is tried once. The figure's ACCESS.AUTHLIM allows only so many wrong passwords before
        // locking for good, so a refused password is never sent again, and once anything is refused because of the
        // password, reading stops: the figure is returned with what was read (sector 0 holds the amiibo data).
        var sector1Protected = (raw[AmiiboV3Dump.PtI2cPage * 4] & PtI2c2kProt) != 0;
        var password = NtagUid.AmiiboPassword(NtagUid.FromSevenByteUid(raw.AsSpan(0, AmiiboV3Dump.UidLength)));
        var authenticated = false;
        var stopReading = false;

        // After a reselect the authentication is gone; only a password the figure already accepted is sent again.
        async Task ReauthenticateAsync()
        {
            if (authenticated)
            {
                await client.PasswordAuthAsync(password, cancellationToken);
            }
        }

        async Task EnterSector1Async()
        {
            await ReauthenticateAsync();
            await SectorSelectAsync(connection, 1, cancellationToken);
        }

        if (sector1Protected && connection.IsDirectCommandMode)
        {
            try
            {
                await client.PasswordAuthAsync(password, cancellationToken);
                authenticated = true;
            }
            catch (NfcNakException ex)
            {
                System.Diagnostics.Debug.WriteLine($"v3 read: the figure refused the amiibo password for sector 1 ({ex.Message}); stopping here, nothing more is read.");
                stopReading = true;
            }
            catch (NfcException ex) when (ex is not NfcCommandNotSupportedException)
            {
                // No answer at all (not a refusal): the password may not have been checked, but it isn't sent again.
                System.Diagnostics.Debug.WriteLine($"v3 read: no answer to the sector 1 password ({ex.Message}); stopping here, nothing more is read.");
                stopReading = true;
            }
        }

        var sectorSelected = false;
        if (!stopReading)
        {
            try
            {
                await WithRecoveryAsync(connection, async () =>
                {
                    await SectorSelectAsync(connection, 1, cancellationToken);
                    return true;
                }, ReauthenticateAsync, cancellationToken, stopOnNak: sector1Protected);
                sectorSelected = true;
            }
            catch (NfcCommandNotSupportedException) when (!connection.IsDirectCommandMode)
            {
                // Not in direct command mode: sector 1 can't be reached.
            }
            catch (NfcNakException ex) when (sector1Protected)
            {
                System.Diagnostics.Debug.WriteLine($"v3 read: refused while entering protected sector 1 ({ex.Message}); stopping here, nothing more is read.");
                stopReading = true;
            }
            catch (NfcException ex)
            {
                System.Diagnostics.Debug.WriteLine($"v3 read: sector 1 unavailable ({ex.Message}); leaving it blank.");
                await TryReselectAsync(connection, cancellationToken);
            }
        }

        if (sectorSelected)
        {
            try
            {
                (await ReadPagesRecoveringAsync(connection, client, 0x00, 0xFF, 1, sector1, cancellationToken, EnterSector1Async, stopOnNak: sector1Protected))
                    .CopyTo(raw, AmiiboV3Dump.SectorLength);
            }
            catch (NfcNakException ex) when (sector1Protected)
            {
                System.Diagnostics.Debug.WriteLine($"v3 read: sector 1 refused a read despite the password ({ex.Message}); stopping here, nothing more is read.");
                Array.Clear(raw, AmiiboV3Dump.SectorLength, AmiiboV3Dump.SectorLength);
                stopReading = true;
            }
            catch (Exception ex) when (ex is NfcException or TagException && ex is not NfcCommandNotSupportedException)
            {
                System.Diagnostics.Debug.WriteLine($"v3 read: sector 1 couldn't be read ({ex.Message}); leaving it blank.");
                Array.Clear(raw, AmiiboV3Dump.SectorLength, AmiiboV3Dump.SectorLength);
                await TryReselectAsync(connection, cancellationToken);
            }
            finally
            {
                if (!stopReading)
                {
                    try
                    {
                        await SectorSelectAsync(connection, 0, cancellationToken);
                    }
                    catch (NfcException)
                    {
                        await TryReselectAsync(connection, cancellationToken);
                    }
                }
            }
        }

        if (stopReading)
        {
            progress?.Report(1);
            var partial = new AmiiboV3Dump(raw);
            if (!partial.HasAmiiboId)
            {
                throw new NfcException("This NTAG I²C tag isn't a v3 amiibo.");
            }

            return partial;
        }

        // Lock-on last: if SRAM turns out not to be readable the tag NAKs and drops out of the session.
        connection.SetStatus("Reading lock-on...");

        // Real figures wait in pass-through mode with the direction set NFC -> I²C: the reader writes a request to SRAM,
        // the figure's microcontroller reads it over I²C and writes its response back. (Seen: NC_REG 41, NS_REG 21, and
        // SRAM_RF_READY never comes on by itself.) The response is reportedly the same whatever the request, so a blank
        // one in the response's own format is written. SRAM is volatile; nothing on the figure is changed.
        var auth0 = raw[AmiiboV3Dump.Auth0Page * 4 + 3];
        var canWriteSram = authenticated || auth0 > AmiiboV3Dump.SramLastPage;
        if (session != null && canWriteSram && WaitingForLockOnRequest(session))
        {
            if (await TryWriteLockOnRequestAsync(client, cancellationToken))
            {
                session = await ReadSessionAsync(connection, cancellationToken) ?? session;
            }
        }

        var (lockOn, latestSession) = await ReadLockOnCoreAsync(connection, session, lockOnTimeout ?? DefaultLockOnTimeout, cancellationToken);
        lockOn?.CopyTo(raw, AmiiboV3Dump.LockOnOffset);
        if (lockOn == null)
        {
            var last = latestSession ?? session;
            System.Diagnostics.Debug.WriteLine(last == null
                ? "v3 read: no lock-on response (session registers unreadable)."
                : $"v3 read: no lock-on response (NC_REG {last[0]:X2}, NS_REG {last[6]:X2}; authenticated: {authenticated}).");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"v3 read: lock-on response read (CRC {(LockOnResponse.HasValidCrc(lockOn) ? "valid" : "invalid")}).");
        }

        // Keep the session registers as they were when the lock-on was read (pass-through on, SRAM ready).
        latestSession?.CopyTo(raw, AmiiboV3Dump.SessionPage0 * 4);

        progress?.Report(1);
        var dump = new AmiiboV3Dump(raw);
        if (!dump.HasAmiiboId)
        {
            throw new NfcException("This NTAG I²C tag isn't a v3 amiibo.");
        }

        return dump;
    }

    /// <summary>
    /// Attempts per step when a v3 figure refuses commands (it NAKs, then stays silent until reselected, while its
    /// microcontroller has the memory over I²C, typically just after it powers up from the field).
    /// </summary>
    public const int RecoveryAttempts = 5;

    /// <summary>
    /// Runs <paramref name="operation"/>; if the tag refuses or stops answering and the connection can reselect it, waits
    /// (longer each time), reselects, runs <paramref name="restore"/> (e.g. selecting sector 1 again) and tries again.
    /// </summary>
    private static async Task<T> WithRecoveryAsync<T>(INfcTagConnection connection, Func<Task<T>> operation, Func<Task>? restore, CancellationToken cancellationToken, bool stopOnNak = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (ex is NfcException or TagException && ex is not NfcCommandNotSupportedException &&
                                       !(stopOnNak && ex is NfcNakException) &&
                                       attempt < RecoveryAttempts && connection is INfcReselectable)
            {
                System.Diagnostics.Debug.WriteLine($"v3 read: {ex.Message} (attempt {attempt}); reselecting.");
                await Task.Delay(100 * attempt, cancellationToken);
                try
                {
                    await ((INfcReselectable)connection).ReselectAsync(cancellationToken);
                    if (restore != null)
                    {
                        await restore();
                    }
                }
                catch (NfcException) when (attempt + 1 < RecoveryAttempts)
                {
                    // Not back yet; the next attempt waits longer.
                }
            }
        }
    }

    // NC_REG: pass-through data flows from NFC to I²C.
    public const byte NcPassThroughDirNfcToI2c = 0x01;

    /// <summary>
    /// What's written to SRAM to ask a real figure for its lock-on response: 62 zero bytes and their CRC, the response's
    /// own format.
    /// </summary>
    public static byte[] LockOnRequest => LockOnResponse.Normalize([0x00]);

    /// <summary>
    /// True when the figure is in pass-through mode, waiting for the NFC side to fill SRAM, and nothing is ready to read.
    /// </summary>
    public static bool WaitingForLockOnRequest(byte[] session) =>
        (session[0] & NcPassThroughOn) != 0 && (session[0] & NcPassThroughDirNfcToI2c) != 0 && (session[6] & NsSramI2cReady) == 0;

    /// <summary>
    /// Writes <see cref="LockOnRequest"/> to SRAM (pages F0-FF, one WRITE each; the last page hands SRAM to I²C).
    /// </summary>
    private static async Task<bool> TryWriteLockOnRequestAsync(Ntag215Client client, CancellationToken cancellationToken)
    {
        var request = LockOnRequest;
        try
        {
            for (var page = AmiiboV3Dump.SramFirstPage; page <= AmiiboV3Dump.SramLastPage; page++)
            {
                await client.WritePageAsync(page, request.AsMemory((page - AmiiboV3Dump.SramFirstPage) * 4, 4), cancellationToken);
            }

            System.Diagnostics.Debug.WriteLine("v3 read: wrote a lock-on request to SRAM; waiting for the figure's response.");
            return true;
        }
        catch (NfcException ex) when (ex is not NfcCommandNotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"v3 read: couldn't write the lock-on request to SRAM ({ex.Message}).");
            return false;
        }
    }

    /// <summary>
    /// Brings the tag back into the session after a refusal, where the connection can (otherwise a no-op).
    /// </summary>
    private static async Task TryReselectAsync(INfcTagConnection connection, CancellationToken cancellationToken)
    {
        if (connection is INfcReselectable reselectable)
        {
            try
            {
                await reselectable.ReselectAsync(cancellationToken);
            }
            catch (NfcException)
            {
            }
        }
    }

    /// <summary>
    /// Reads pages in reader sized pieces, each retried with <see cref="WithRecoveryAsync"/>, so a figure that refuses
    /// part way through costs one piece rather than the whole read.
    /// </summary>
    private static async Task<byte[]> ReadPagesRecoveringAsync(
        INfcTagConnection connection,
        Ntag215Client client,
        int startPage,
        int endPage,
        byte sector,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Func<Task>? restore = null,
        bool stopOnNak = false)
    {
        var total = endPage - startPage + 1;
        var output = new byte[total * 4];

        // A READ returns 4 pages, so readers without FAST_READ go 4 at a time (which also keeps sector 0 before EA).
        var batch = connection.MaxFastReadPages > 1 ? connection.MaxFastReadPages : 4;
        restore ??= sector == 0 ? null : () => SectorSelectAsync(connection, sector, cancellationToken);
        for (var page = startPage; page <= endPage; page += batch)
        {
            var first = page;
            var last = Math.Min(endPage, page + batch - 1);
            var chunk = await WithRecoveryAsync(connection, () => client.ReadPagesAsync(first, last, null, cancellationToken), restore, cancellationToken, stopOnNak);
            chunk.CopyTo(output, (first - startPage) * 4);
            progress?.Report((double)(last - startPage + 1) / total);
        }

        return output;
    }

    /// <summary>
    /// Waits for the figure to publish its lock-on response in SRAM and reads it (64 bytes), or returns null.
    /// </summary>
    /// <remarks>
    /// In pass-through mode (NC_REG.PTHRU_ON_OFF) SRAM is at sector 0 pages F0-FF and NS_REG.SRAM_I2C_READY says when
    /// the I²C side has filled it; in mirror mode (NC_REG.SRAM_MIRROR_ON_OFF) it's at SM_REG and can be read directly.
    /// The session registers are re-read until one of those is true or the timeout passes. Readers that can't
    /// FAST_READ the session registers just try F0-FF.
    /// </remarks>
    public static async Task<byte[]?> ReadLockOnAsync(INfcTagConnection connection, byte[]? session, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        (await ReadLockOnCoreAsync(connection, session, timeout, cancellationToken)).LockOn;

    /// <summary>
    /// <see cref="ReadLockOnAsync"/>, also returning the last session registers read while waiting.
    /// </summary>
    private static async Task<(byte[]? LockOn, byte[]? Session)> ReadLockOnCoreAsync(INfcTagConnection connection, byte[]? session, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (session == null)
        {
            return (await TryReadSramAsync(connection, AmiiboV3Dump.SramFirstPage, cancellationToken), null);
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var nc = session[0];
            var sm = session[2];
            var ns = session[6];

            if ((nc & NcPassThroughOn) != 0 && (ns & NsSramI2cReady) != 0)
            {
                return (await TryReadSramAsync(connection, AmiiboV3Dump.SramFirstPage, cancellationToken), session);
            }

            if ((nc & NcPassThroughOn) == 0 && (nc & NcSramMirrorOn) != 0 && sm <= 0xF0)
            {
                return (await TryReadSramAsync(connection, sm, cancellationToken), session);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return (null, session);
            }

            await Task.Delay(50, cancellationToken);
            var next = await ReadSessionAsync(connection, cancellationToken);
            if (next == null)
            {
                return (null, session);
            }

            session = next;
        }
    }

    /// <summary>
    /// Header, write counter and the first part of the encrypted settings (raw 0x010..0x033).
    /// </summary>
    public const int AppHeaderFirstPage = 0x04;
    public const int AppHeaderLastPage = 0x0C;

    /// <summary>
    /// Data HMAC and the encrypted body in the v3 layout (raw 0x0C0..0x247).
    /// </summary>
    public const int AppBodyFirstPage = 0x30;
    public const int AppBodyLastPage = 0x91;

    /// <summary>
    /// Writes <paramref name="source"/>'s app data to the v3 figure on the reader. v3 amiibo can't be created on blank
    /// tags: the target has to be a v3 figure of the same character. Unless it's the same figure (same UID and salt),
    /// the data is decrypted and re-encrypted for the target's UID and salt, which needs the keys. Only the header and
    /// encrypted data pages are written; the UID, ID, salt, tag HMAC, lock bits and lock-on are never touched.
    /// </summary>
    public static async Task<NfcWriteKind> WriteAppDataAsync(
        INfcTagConnection connection,
        AmiiboV3Dump source,
        AmiiboKeySet? keys,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AmiiboNfcOperations.EnsureDirectCommandMode(connection);
        var client = new Ntag215Client(connection);

        var version = await client.GetVersionAsync(cancellationToken);
        if (version == null || !version.IsNtagI2cPlus2k)
        {
            throw new NfcException("v3 amiibo data can only be written to a v3 amiibo figure (NTAG I²C Plus 2K).");
        }

        connection.SetStatus("Reading figure...");
        var raw = new byte[AmiiboV3Dump.ImageLength];
        (await client.ReadPagesAsync(0x00, AmiiboV3Dump.PtI2cPage, cancellationToken: cancellationToken)).CopyTo(raw, 0);
        var target = new AmiiboV3Dump(raw);

        if (!target.HasAmiiboId)
        {
            throw new NfcException("This tag isn't a v3 amiibo figure. v3 amiibo can't be written to blank tags; only the app data of an existing figure can be replaced.");
        }

        if (target.AmiiboIdHex != source.AmiiboIdHex)
        {
            throw new NfcException("This figure is a different character.");
        }

        byte[] appRaw;
        var sameFigure = target.Span[..AmiiboV3Dump.UidLength].SequenceEqual(source.Span[..AmiiboV3Dump.UidLength]) &&
                         target.Span.Slice(0x060, 0x20).SequenceEqual(source.Span.Slice(0x060, 0x20));
        if (sameFigure)
        {
            appRaw = source.Data;
        }
        else
        {
            if (keys == null)
            {
                throw new NfcException("This is a different figure, so the data has to be re-encrypted for it. Load your key file first.");
            }

            var internalData = AmiiboCrypto.RetargetAppData(source.ToInternal(), target.ToInternal(), keys);
            appRaw = AmiiboCrypto.FromInternal(internalData, raw, AmiiboCrypto.NtagI2cPlusLayout);
        }

        var auth0 = raw[AmiiboV3Dump.Auth0Page * 4 + 3];
        if (auth0 <= AppBodyLastPage)
        {
            var password = NtagUid.AmiiboPassword(NtagUid.FromSevenByteUid(target.Uid));
            try
            {
                await client.PasswordAuthAsync(password, cancellationToken);
            }
            catch (NfcException ex)
            {
                throw new NfcException("The figure didn't accept the amiibo password.", ex);
            }
        }

        connection.SetStatus("Writing app data...");
        var writes = new List<(int, byte[])>();
        for (var page = AppHeaderFirstPage; page <= AppHeaderLastPage; page++)
        {
            writes.Add((page, appRaw.AsSpan(page * 4, 4).ToArray()));
        }

        for (var page = AppBodyFirstPage; page <= AppBodyLastPage; page++)
        {
            writes.Add((page, appRaw.AsSpan(page * 4, 4).ToArray()));
        }

        await client.WritePagesAsync(writes, progress, cancellationToken);
        return NfcWriteKind.AppData;
    }

    /// <summary>
    /// SECTOR_SELECT (C2 FF, then the sector number). The second packet's "passive ACK" is silence, which
    /// connections report as a timeout, so that's treated as success. A NAK is a failure.
    /// </summary>
    public static async Task SectorSelectAsync(INfcTagConnection connection, byte sector, CancellationToken cancellationToken = default)
    {
        var first = await connection.TransceiveAsync([CmdSectorSelect, 0xFF], cancellationToken);
        if (!(first.Length == 0 || (first.Length == 1 && (first[0] & 0x0F) == 0x0A)))
        {
            throw new NfcException($"SECTOR_SELECT was refused ({Hex.ToSpacedHex(first)}).");
        }

        byte[] second;
        try
        {
            second = await connection.TransceiveAsync([sector, 0x00, 0x00, 0x00], cancellationToken);
        }
        catch (NfcCommandNotSupportedException)
        {
            throw;
        }
        catch (NfcException)
        {
            return; // Passive ACK.
        }

        if (second.Length == 1 && (second[0] & 0x0F) != 0x0A)
        {
            throw new NfcException($"Sector {sector} doesn't exist on this tag.");
        }
    }

    /// <summary>
    /// Session registers EC-ED (8 bytes), or null if the reader can't FAST_READ them. A 4 page READ can't be used:
    /// every READ window that includes EC-ED also includes an invalid page.
    /// </summary>
    private static Task<byte[]?> ReadSessionAsync(INfcTagConnection connection, CancellationToken cancellationToken) =>
        ReadWindowAsync(connection, AmiiboV3Dump.SessionPage0, AmiiboV3Dump.SessionPage1, readStart: null, cancellationToken);

    /// <summary>
    /// Reads pages <paramref name="first"/>..<paramref name="last"/> with FAST_READ, or with a 4 page READ starting at
    /// <paramref name="readStart"/> (chosen so the window has no invalid pages) when FAST_READ isn't available.
    /// </summary>
    private static async Task<byte[]?> ReadWindowAsync(INfcTagConnection connection, int first, int last, int? readStart, CancellationToken cancellationToken)
    {
        var length = (last - first + 1) * 4;
        try
        {
            var data = await connection.TransceiveAsync([Ntag215Client.CmdFastRead, (byte)first, (byte)last], cancellationToken);
            return data.Length == length ? data : null;
        }
        catch (NfcCommandNotSupportedException) when (readStart != null)
        {
            try
            {
                var data = await connection.TransceiveAsync([Ntag215Client.CmdRead, (byte)readStart.Value], cancellationToken);
                var offset = (first - readStart.Value) * 4;
                return data.Length >= offset + length ? data.AsSpan(offset, length).ToArray() : null;
            }
            catch (NfcException)
            {
                return null;
            }
        }
        catch (NfcException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryReadSramAsync(INfcTagConnection connection, int firstPage, CancellationToken cancellationToken)
    {
        var sram = new byte[LockOnResponse.Length];
        try
        {
            // Halves, because some bridges (the MFRC522's 64 byte FIFO) can't return 16 pages at once.
            for (var half = 0; half < 2; half++)
            {
                var start = firstPage + half * 8;
                byte[] data;
                try
                {
                    data = await connection.TransceiveAsync([Ntag215Client.CmdFastRead, (byte)start, (byte)(start + 7)], cancellationToken);
                }
                catch (NfcCommandNotSupportedException)
                {
                    var first = await connection.TransceiveAsync([Ntag215Client.CmdRead, (byte)start], cancellationToken);
                    var second = await connection.TransceiveAsync([Ntag215Client.CmdRead, (byte)(start + 4)], cancellationToken);
                    if (first.Length < 16 || second.Length < 16)
                    {
                        return null;
                    }

                    data = [.. first.AsSpan(0, 16), .. second.AsSpan(0, 16)];
                }

                if (data.Length != 32)
                {
                    return null;
                }

                data.CopyTo(sram, half * 32);
            }
        }
        catch (NfcException)
        {
            return null;
        }

        return sram;
    }
}
