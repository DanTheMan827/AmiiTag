namespace AmiiTag.Nfc;

/// <summary>
/// A pluggable source of NFC tag connections (CoreNFC, Android, PC/SC readers, an MFRC522 over serial...).
/// Register any number of these in DI; the app lists the ones that report <see cref="IsSupported"/>.
/// </summary>
public interface INfcProvider
{
    /// <summary>
    /// A stable identifier, used to remember the user's choice.
    /// </summary>
    string Id
    {
        get;
    }

    /// <summary>
    /// A user facing name, such as "PC/SC reader (ACR122U, ACR1552U)".
    /// </summary>
    string DisplayName
    {
        get;
    }

    /// <summary>
    /// True if this provider can be used on this device right now.
    /// </summary>
    bool IsSupported
    {
        get;
    }

    /// <summary>
    /// True when the OS shows its own "hold your tag near..." UI (iOS), so the app should not show one.
    /// </summary>
    bool ShowsSystemPrompt
    {
        get;
    }

    /// <summary>
    /// True when <see cref="GetReadersAsync"/> returns a meaningful list the user should pick from
    /// (PC/SC readers, COM ports). Phone NFC returns a single implicit reader.
    /// </summary>
    bool SupportsReaderSelection
    {
        get;
    }

    /// <summary>
    /// Lists the readers (or ports) this provider can use.
    /// </summary>
    Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for a tag to be presented and connects to it.
    /// </summary>
    Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Allows a provider to request a user gesture first (Web Serial needs one to pick a port).
/// </summary>
public interface INfcReaderRequester
{
    /// <summary>
    /// True where readers have to be granted first (Web Serial). Elsewhere every reader is already listed.
    /// </summary>
    bool CanRequestReader => true;

    /// <summary>
    /// Asks the user to grant access to a new reader. Must be called from a user gesture in the browser.
    /// </summary>
    Task<NfcReaderInfo?> RequestReaderAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by serial readers whose link speed can be chosen in Settings (the MFRC522's UART).
/// </summary>
public interface INfcBaudRateSetting
{
    int BaudRate
    {
        get; set;
    }

    int DefaultBaudRate
    {
        get;
    }

    IReadOnlyList<int> SupportedBaudRates
    {
        get;
    }
}

public sealed record NfcReaderInfo(string Id, string DisplayName);

public sealed class NfcConnectOptions
{
    /// <summary>
    /// The reader to use, from <see cref="INfcProvider.GetReadersAsync"/>. Null picks the first available reader.
    /// </summary>
    public string? ReaderId
    {
        get; init;
    }

    /// <summary>
    /// The message shown while waiting (used by system prompts).
    /// </summary>
    public string Prompt { get; init; } = "Hold your tag near the reader.";

    /// <summary>
    /// False for background monitoring: never open a picker or other prompt (e.g. the Web Serial port chooser).
    /// </summary>
    public bool AllowUserPrompts { get; init; } = true;
}

/// <summary>
/// Implemented by connections that can activate the same tag again after it dropped out of the session (an NTAG goes
/// idle after any NAK). v3 amiibo figures need this: while the figure's microcontroller is using the NTAG I²C Plus
/// memory over I²C, NFC commands are refused, so a read has to wait, reselect the tag and carry on.
/// </summary>
public interface INfcReselectable
{
    /// <summary>Whether the active transport supports reselecting without requesting an RF-field reset.</summary>
    bool CanReselect => true;

    /// <summary>
    /// Wakes and selects the tag again, without cycling the field (that would reset a v3 figure's microcontroller).
    /// Throws if no tag, or a different one, answers.
    /// </summary>
    Task ReselectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by providers that can tell whether a tag is on a reader without connecting to it (PC/SC reader status).
/// The background monitor uses it to wait for a tag to be taken off: connecting every half second makes some readers
/// restart polling and beep each time.
/// </summary>
public interface INfcPresenceProvider
{
    /// <returns>True or false, or null if the provider can't tell right now (then the monitor connects instead).</returns>
    Task<bool?> IsTagPresentAsync(string? readerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implemented by providers that keep something open between scans (a COM port, a session). The provider reopens it
/// on its next connect; this just lets it go, e.g. when the user picks a different reader.
/// </summary>
public interface INfcReleasable
{
    Task ReleaseAsync();
}

/// <summary>
/// A connection to a single ISO14443-A tag. Everything above this works in terms of raw NFC-A commands
/// (without CRC), so NTAG215 logic only has to be written once.
/// </summary>
public interface INfcTagConnection : IAsyncDisposable
{
    /// <summary>
    /// The UID reported by the reader (normally 7 bytes), or empty if unknown.
    /// </summary>
    byte[] Uid
    {
        get;
    }

    /// <summary>
    /// The largest number of pages that can be requested in one FAST_READ.
    /// </summary>
    int MaxFastReadPages
    {
        get;
    }

    /// <summary>
    /// True when the reader is in direct command mode: raw NFC-A commands go to the tag as they are, so every
    /// command (WRITE, PWD_AUTH, GET_VERSION, SECTOR_SELECT...) is possible. False when the reader only offers its own
    /// higher level read commands, which is enough to read a tag but not to write one.
    /// </summary>
    bool IsDirectCommandMode => true;

    /// <summary>
    /// Sends a raw command and returns the tag's response (CRC removed). A 4 bit ACK is returned as a single byte.
    /// </summary>
    /// <exception cref="NfcCommandNotSupportedException">The reader can't send this command.</exception>
    Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a raw command without an expected response.
    /// </summary>
    /// <exception cref="NfcCommandNotSupportedException">The reader can't send this command.</exception>
    /// <returns></returns>
    Task SendAsync(byte[] command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a command with a temporary response timeout where supported. Transports without a configurable
    /// timeout use their normal exchange; cancellation must not abandon a native operation still in flight.
    /// </summary>
    Task<byte[]> TransceiveAsync(byte[] command, TimeSpan responseTimeout, CancellationToken cancellationToken = default) =>
        TransceiveAsync(command, cancellationToken);

    /// <summary>
    /// Updates the text of a system prompt, if there is one.
    /// </summary>
    void SetStatus(string message);

    /// <summary>
    /// Ends the session. On iOS a non-null <paramref name="errorMessage"/> is shown in the system sheet.
    /// </summary>
    Task CloseAsync(string? errorMessage = null);
}

public class NfcException : Exception
{
    public NfcException(string message) : base(message)
    {
    }

    public NfcException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The tag answered a command with a NAK (4 bit, not ACK): it refused it, as opposed to not answering at all.
/// </summary>
public class NfcNakException : NfcException
{
    public NfcNakException(byte nak, string message) : base(message)
    {
        Nak = nak;
    }

    public byte Nak
    {
        get;
    }
}

/// <summary>
/// Thrown when a reader can only perform a subset of raw commands.
/// </summary>
public class NfcCommandNotSupportedException : NfcException
{
    public NfcCommandNotSupportedException(byte command)
        : base($"This reader can't send NFC command 0x{command:X2}.")
    {
        Command = command;
    }

    public byte Command
    {
        get;
    }
}

/// <summary>
/// The reader chip didn't answer on the port at all (none of the rates tried), as opposed to a tag or protocol error:
/// whatever is on the port isn't that kind of reader. <see cref="AutoDetectSerialNfcProvider"/> then tries the next kind.
/// </summary>
public class NfcReaderNotFoundException : NfcException
{
    public NfcReaderNotFoundException(string message, string portId) : base(message)
    {
        PortId = portId;
    }

    /// <summary>
    /// The port that was tried.
    /// </summary>
    public string PortId
    {
        get;
    }
}

/// <summary>The transport session was lost; silence must not be treated as a passive ACK in this case.</summary>
public class NfcConnectionLostException : NfcException
{
    public NfcConnectionLostException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>A native tag/protocol error, not silence acknowledging a sector selection.</summary>
public class NfcTagResponseException : NfcException
{
    public NfcTagResponseException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A serial reader that can tell quickly whether it's the one on a port, before any full setup is tried.
/// <see cref="AutoDetectSerialNfcProvider"/> probes with it first.
/// </summary>
public interface INfcSerialProbe
{
    /// <summary>
    /// True if this kind of reader answered on the port; it then stays open for the next ConnectAsync. False (and the
    /// port closed again) if nothing answered as expected within the probe's short time limit.
    /// </summary>
    Task<bool> ProbeAsync(string portId, CancellationToken cancellationToken = default);
}
