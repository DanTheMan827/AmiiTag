using AmiiTag.Tags;

namespace AmiiTag.Nfc;

/// <summary>
/// The result of writing to a tag over NFC.
/// </summary>
public enum NfcWriteKind
{
    /// <summary>A blank NTAG215 was fully written and locked as an amiibo.</summary>
    FullWrite,

    /// <summary>An existing amiibo had its app data replaced.</summary>
    AppData,

    /// <summary>A puck in NFC write mode received the raw dump.</summary>
    Puck,
}

/// <summary>
/// Reading and writing amiibo over any <see cref="INfcTagConnection"/>.
/// Port of the Swift <c>NTAG215Tag</c>, <c>NFCTagReader</c> and the TagInfo write flow.
/// </summary>
public static class AmiiboNfcOperations
{
    /// <summary>
    /// Reads the full tag (pages 0-134) plus the originality signature.
    /// </summary>
    public static async Task<TagDump> ReadTagAsync(INfcTagConnection connection, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = new Ntag215Client(connection);

        var version = await client.GetVersionAsync(cancellationToken);
        if (version != null && !version.IsNtag215)
        {
            throw new NfcException("This is not an NTAG215 tag.");
        }

        connection.SetStatus("Reading tag...");
        var data = await client.ReadPagesAsync(0, Ntag215Client.LastPage, progress, cancellationToken);
        var signature = await client.ReadSignatureAsync(cancellationToken) ?? new byte[TagDump.SignatureLength];

        var full = new byte[TagDump.FullLength];
        data.CopyTo(full, 0);
        signature.CopyTo(full, TagDump.DataLength);

        // Some readers return PWD as zeros, some as garbage; amiibo dumps normally keep PWD/PACK zeroed.
        return new TagDump(full);
    }

    /// <summary>
    /// Checks if the tag is a puck in NFC write mode (it answers FAST_READ of pages 133-134 with 01..08).
    /// </summary>
    public static async Task<bool> IsPuckAsync(INfcTagConnection connection, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await connection.TransceiveAsync([Ntag215Client.CmdFastRead, 133, 134], cancellationToken);
            return response.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        }
        catch (NfcCommandNotSupportedException)
        {
            return false;
        }
        catch (NfcException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="source"/> to whatever tag is connected, following the Swift logic:
    /// pucks get the raw dump, locked amiibo get their app data replaced, blank tags get a full write.
    /// </summary>
    public static async Task<NfcWriteKind> WriteTagAsync(
        INfcTagConnection connection,
        TagDump source,
        AmiiboKeySet? keys,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectCommandMode(connection);
        var client = new Ntag215Client(connection);

        if (await IsPuckAsync(connection, cancellationToken))
        {
            connection.SetStatus("Writing to puck...");
            var raw = source.ToArray(TagDump.FullLength);
            var pages = new List<(int, byte[])>();
            for (var page = 0; page < TagDump.FullLength / TagDump.PageSize; page++)
            {
                pages.Add((page, raw.AsSpan(page * 4, 4).ToArray()));
            }

            await client.WritePagesAsync(pages, progress, cancellationToken);

            // Tells the puck firmware to save and leave write mode. It doesn't reliably answer.
            try
            {
                await connection.TransceiveAsync([0x88], cancellationToken);
            }
            catch (NfcException)
            {
            }

            return NfcWriteKind.Puck;
        }

        var version = await client.GetVersionAsync(cancellationToken);
        if (version != null && !version.IsNtag215)
        {
            throw new NfcException("This is not an NTAG215 tag.");
        }

        connection.SetStatus("Reading tag...");
        var current = new TagDump(await client.ReadPagesAsync(0, Ntag215Client.LastPage, cancellationToken: cancellationToken));

        // What's on the tag decides the write: a locked (or password protected) amiibo only takes new app data; an
        // unlocked tag gets the whole image with its lock and configuration pages written last.
        var locks = DescribeLocks(current);
        if (locks.Count > 0)
        {
            if (!AmiiboValidator.Validate(current, keys).IsAmiibo)
            {
                throw new NfcException($"This tag isn't an amiibo and can't be rewritten: {string.Join(", ", locks)}.");
            }

            if (current.AmiiboIdHex != source.AmiiboIdHex)
            {
                throw new NfcException("This tag is a different character.");
            }

            // Same figure: the data is already encrypted for it. Otherwise decrypt and re-encrypt for the target's UID
            // (and its keygen salt, which is locked on the figure along with the ID and tag HMAC).
            TagDump appData;
            var sameFigure = current.Uid.AsSpan().SequenceEqual(source.Uid) &&
                             current.Span.Slice(0x060, 0x20).SequenceEqual(source.Span.Slice(0x060, 0x20));
            if (sameFigure)
            {
                appData = source;
            }
            else
            {
                if (keys == null)
                {
                    throw new NfcException("This is a different figure, so the data has to be re-encrypted for it. Load your key file first.");
                }

                var internalData = AmiiboCrypto.RetargetAppData(
                    AmiiboCrypto.ToInternal(source.Span, AmiiboCrypto.Ntag215Layout),
                    AmiiboCrypto.ToInternal(current.Span, AmiiboCrypto.Ntag215Layout),
                    keys);
                appData = new TagDump(AmiiboCrypto.FromInternal(internalData, current.Span, AmiiboCrypto.Ntag215Layout));
            }

            connection.SetStatus("Writing app data...");
            await WriteAppDataAsync(client, current.Uid, appData, progress, cancellationToken);
            return NfcWriteKind.AppData;
        }

        if (keys == null)
        {
            throw new NfcException("A key file is required to write to a blank tag.");
        }

        connection.SetStatus("Writing tag...");
        var patched = source.Patch(current.Uid, keys);
        await WriteFullAsync(client, patched, progress, cancellationToken);
        return NfcWriteKind.FullWrite;
    }

    /// <summary>
    /// What on an NTAG215 image stops a full write: lock bits (static, dynamic, block-locking), the configuration lock,
    /// or password protection. Empty for a tag that can be written from scratch.
    /// </summary>
    public static IReadOnlyList<string> DescribeLocks(TagDump tag)
    {
        var raw = tag.ToArray(TagDump.DataLength);
        var locks = new List<string>();
        if (raw[10] != 0 || raw[11] != 0)
        {
            locks.Add("static lock bits are set");
        }

        if (raw[520] != 0 || raw[521] != 0 || raw[522] != 0)
        {
            locks.Add("dynamic lock bits are set");
        }

        if (raw[527] <= Ntag215Client.LastPage)
        {
            locks.Add($"pages from {raw[527]} are password protected");
        }

        if ((raw[528] & 0x40) != 0)
        {
            locks.Add("the configuration is locked");
        }

        return locks;
    }

    /// <summary>
    /// Writing needs raw commands (password authentication, page writes in a specific order, lock bits).
    /// </summary>
    public static void EnsureDirectCommandMode(INfcTagConnection connection)
    {
        if (!connection.IsDirectCommandMode)
        {
            throw new NfcException("This reader isn't in direct command mode, so it can read tags but not write them.");
        }
    }

    private static async Task WriteAppDataAsync(Ntag215Client client, byte[] uid, TagDump dump, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var pack = await client.PasswordAuthAsync(NtagUid.AmiiboPassword(uid), cancellationToken);
        if (pack.Length < 2 || pack[0] != 0x80 || pack[1] != 0x80)
        {
            throw new NfcException("The tag did not accept the amiibo password.");
        }

        var data = dump.ToArray(TagDump.FullLength);
        var writes = new List<(int, byte[])>();
        for (var page = 4; page <= 12; page++)
        {
            writes.Add((page, data.AsSpan(page * 4, 4).ToArray()));
        }

        for (var page = 32; page <= 129; page++)
        {
            writes.Add((page, data.AsSpan(page * 4, 4).ToArray()));
        }

        await client.WritePagesAsync(writes, progress, cancellationToken);
    }

    /// <summary>
    /// Writes a whole amiibo to an unlocked tag. The data goes first; the pages that protect it are written last, so
    /// nothing locks before everything is in place: CC, PWD/PACK, CFG0 (AUTH0), CFG1 (ACCESS, incl. CFGLCK), the
    /// dynamic lock bits, and finally the static lock bits. (AUTH0 and CFGLCK only take effect at the next activation.)
    /// </summary>
    private static async Task WriteFullAsync(Ntag215Client client, TagDump patched, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var data = patched.ToArray(TagDump.FullLength);
        var writes = new List<(int, byte[])>();
        for (var page = 4; page < 130; page++)
        {
            writes.Add((page, data.AsSpan(page * 4, 4).ToArray()));
        }

        writes.Add((3, data.AsSpan(12, 4).ToArray()));                              // Capability container (OTP)
        writes.Add((134, new byte[] { 0x80, 0x80, 0x00, 0x00 }));                   // PACK / RFUI
        writes.Add((133, NtagUid.AmiiboPassword(patched.Uid)));                     // PWD
        writes.Add((131, new byte[] { 0x00, 0x00, 0x00, 0x04 }));                   // CFG0: AUTH0 = 4
        writes.Add((132, new byte[] { 0x5F, 0x00, 0x00, 0x00 }));                   // CFG1: CFGLCK, NFC counter, AUTHLIM
        writes.Add((130, new byte[] { 0x01, 0x00, 0x0F, 0x00 }));                   // Dynamic lock bits
        writes.Add((2, new byte[] { data[8], data[9], 0x0F, 0xE0 }));               // Static lock bits, last

        await client.WritePagesAsync(writes, progress, cancellationToken);
    }
}
