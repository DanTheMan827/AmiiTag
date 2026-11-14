using AmiiTag.Nfc;

namespace AmiiTag.Tests.Simulation;

/// <summary>
/// An <see cref="INfcTagConnection"/> straight onto a simulated tag, behaving like Android NfcA / CoreNFC
/// (CRC handled by the reader, 4 bit ACK/NAK returned as one byte, silence reported as an error).
/// </summary>
public sealed class SimulatedTagConnection : INfcTagConnection
{
    private readonly TagSimulator _tag;
    private readonly HashSet<byte> _unsupported;

    public SimulatedTagConnection(TagSimulator tag, int maxFastReadPages = 32, IEnumerable<byte>? unsupportedCommands = null)
    {
        _tag = tag;
        MaxFastReadPages = maxFastReadPages;
        _unsupported = unsupportedCommands?.ToHashSet() ?? new HashSet<byte>();
    }

    public byte[] Uid => _tag.Uid7;

    public int MaxFastReadPages { get; }

    /// <summary>
    /// False simulates a reader that isn't in direct command mode (e.g. PC/SC storage card commands):
    /// only READ reaches the tag.
    /// </summary>
    public bool IsDirectCommandMode { get; init; } = true;

    public List<string> StatusMessages { get; } = [];

    public bool Closed { get; private set; }

    public string? CloseError { get; private set; }

    public Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Length > 0 && (_unsupported.Contains(command[0]) || (!IsDirectCommandMode && command[0] != 0x30)))
        {
            throw new NfcCommandNotSupportedException(command[0]);
        }

        var response = _tag.Transceive(command);
        return response.Kind switch
        {
            TagResponseKind.None => throw new NfcException("The tag didn't respond."),
            TagResponseKind.Data => Task.FromResult(response.Data),
            _ => Task.FromResult(new[] { response.Nibble }),
        };
    }

    public void SetStatus(string message) => StatusMessages.Add(message);

    public Task CloseAsync(string? errorMessage = null)
    {
        Closed = true;
        CloseError = errorMessage;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
