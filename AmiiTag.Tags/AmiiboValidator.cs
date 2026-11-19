namespace AmiiTag.Tags;

public enum AmiiboValidity
{
    /// <summary>A factory blank (or wiped) tag: no amiibo data at all.</summary>
    Blank,

    /// <summary>Some other data: not laid out like an amiibo.</summary>
    NotAmiibo,

    /// <summary>Laid out like an amiibo, but the keys aren't loaded so its signatures can't be checked.</summary>
    Unverified,

    /// <summary>Laid out like an amiibo, but its data doesn't match its signatures (damaged or tampered with).</summary>
    Corrupt,

    /// <summary>An amiibo whose tag and data signatures check out with the retail keys.</summary>
    Valid,
}

/// <summary>
/// The outcome of <see cref="AmiiboValidator.Validate"/>.
/// </summary>
/// <param name="Status">Overall verdict.</param>
/// <param name="Problems">Why it isn't valid (empty when <see cref="Status"/> is <see cref="AmiiboValidity.Valid"/>).</param>
/// <param name="Notes">Non-fatal observations, e.g. an amiibo dump whose lock bytes were never written.</param>
public sealed record AmiiboValidation(AmiiboValidity Status, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// The image has amiibo structure, so its ID can be used to describe it (valid, unverified or corrupt).
    /// </summary>
    public bool IsAmiibo => Status is AmiiboValidity.Valid or AmiiboValidity.Unverified or AmiiboValidity.Corrupt;

    public string Summary => Status switch
    {
        AmiiboValidity.Valid => "Valid amiibo",
        AmiiboValidity.Unverified => "Amiibo (not verified: keys not loaded)",
        AmiiboValidity.Corrupt => "Damaged amiibo data",
        AmiiboValidity.Blank => "Blank tag",
        _ => "Not an amiibo",
    };
}

/// <summary>
/// Checks that a tag image really is an amiibo before anything is shown or written from it: the NXP UID and its check
/// bytes, the amiibo capability container and header magic, an amiibo ID of the right kind, and, when the keys are
/// loaded, both HMACs (tag and data signatures).
/// </summary>
public static class AmiiboValidator
{
    private static readonly byte[] AmiiboCc = [0xF1, 0x10, 0xFF, 0xEE];
    private const byte HeaderMagic = 0xA5;

    public static AmiiboValidation Validate(IAmiiboImage image, AmiiboKeySet? keys) => image switch
    {
        TagDump dump => Validate(dump, keys),
        AmiiboV3Dump v3 => Validate(v3, keys),
        _ => new AmiiboValidation(AmiiboValidity.NotAmiibo, ["Unknown tag image."], []),
    };

    public static AmiiboValidation Validate(TagDump dump, AmiiboKeySet? keys)
    {
        var raw = dump.Span;
        var problems = new List<string>();
        var notes = new List<string>();

        if (IsBlank(raw[0x0C..0x10], raw[0x10..0x208]))
        {
            return new AmiiboValidation(AmiiboValidity.Blank, ["The tag holds no amiibo data."], []);
        }

        // UID: NXP manufacturer byte and both block check characters.
        if (raw[0] != 0x04)
        {
            notes.Add($"The UID doesn't start with NXP's 04 (it starts with {raw[0]:X2}).");
        }

        if (raw[3] != (byte)(0x88 ^ raw[0] ^ raw[1] ^ raw[2]) || raw[8] != (byte)(raw[4] ^ raw[5] ^ raw[6] ^ raw[7]))
        {
            problems.Add("The UID check bytes are wrong.");
        }

        CheckCommon(raw, dump.HasAmiiboId, "02", problems);

        if (problems.Count == 0)
        {
            // An amiibo image that was never locked (e.g. from an emulator) is still usable; just say so.
            if (!dump.IsAmiibo)
            {
                notes.Add("The lock and configuration bytes aren't the ones a real amiibo has.");
            }

            if (dump.Length >= TagDump.FullLength && dump.HasSignature && !dump.VerifyOriginality())
            {
                notes.Add("The NXP originality signature doesn't match the UID.");
            }
        }

        return Finish(problems, notes, keys, () => AmiiboCrypto.ToInternal(dump.Span, AmiiboCrypto.Ntag215Layout));
    }

    public static AmiiboValidation Validate(AmiiboV3Dump dump, AmiiboKeySet? keys)
    {
        var raw = dump.Span;
        var problems = new List<string>();
        var notes = new List<string>();

        if (IsBlank(raw[0x0C..0x10], raw[0x10..0x248]))
        {
            return new AmiiboValidation(AmiiboValidity.Blank, ["The tag holds no amiibo data."], []);
        }

        if (raw[0] != 0x04)
        {
            notes.Add($"The UID doesn't start with NXP's 04 (it starts with {raw[0]:X2}).");
        }

        CheckCommon(raw, dump.HasAmiiboId, "03", problems);
        return Finish(problems, notes, keys, dump.ToInternal);
    }

    private static void CheckCommon(ReadOnlySpan<byte> raw, bool hasAmiiboId, string idSuffix, List<string> problems)
    {
        if (!raw[0x0C..0x10].SequenceEqual(AmiiboCc))
        {
            problems.Add("The capability container isn't an amiibo's (F1 10 FF EE).");
        }

        if (raw[0x10] != HeaderMagic)
        {
            problems.Add("The amiibo header magic (A5) is missing.");
        }

        if (!hasAmiiboId)
        {
            problems.Add($"There's no amiibo ID (it must end in {idSuffix}).");
        }
    }

    private static AmiiboValidation Finish(List<string> problems, List<string> notes, AmiiboKeySet? keys, Func<byte[]> internalData)
    {
        if (problems.Count > 0)
        {
            return new AmiiboValidation(AmiiboValidity.NotAmiibo, problems, notes);
        }

        if (keys is not { CanEncrypt: true })
        {
            return new AmiiboValidation(AmiiboValidity.Unverified, ["The keys aren't loaded, so the signatures couldn't be checked."], notes);
        }

        bool authentic;
        try
        {
            authentic = AmiiboCrypto.Authenticate(internalData(), keys);
        }
        catch (TagException)
        {
            authentic = false;
        }

        return authentic
            ? new AmiiboValidation(AmiiboValidity.Valid, [], notes)
            : new AmiiboValidation(AmiiboValidity.Corrupt, ["The data doesn't match its signatures (damaged, or changed without re-signing)."], notes);
    }

    /// <summary>
    /// No amiibo CC, no header magic, and an amiibo data area that's empty: all zeros, or just an empty NDEF message
    /// (03 00 FE) as many blank tags ship with. A factory blank or wiped tag.
    /// </summary>
    private static bool IsBlank(ReadOnlySpan<byte> cc, ReadOnlySpan<byte> data)
    {
        if (cc.SequenceEqual(AmiiboCc) || data[0] == HeaderMagic)
        {
            return false;
        }

        var rest = data.StartsWith(EmptyNdef.AsSpan()) ? data[EmptyNdef.Length..] : data;
        return rest.IndexOfAnyExcept((byte)0) < 0;
    }

    private static readonly byte[] EmptyNdef = [0x03, 0x00, 0xFE];
}
