using PCSC;
using PCSC.Exceptions;

namespace AmiiTag.Nfc.PcSc;

/// <summary>
/// NFC through any PC/SC contactless reader. Raw NTAG commands are tunnelled through a
/// reader specific <see cref="IPcscTransceiveStrategy"/> chosen by <see cref="PcscStrategySelector"/>.
/// </summary>
public sealed class PcscNfcProvider : INfcProvider
{
    private readonly PcscStrategySelector _selector;

    public PcscNfcProvider() : this(new PcscStrategySelector())
    {
    }

    public PcscNfcProvider(PcscStrategySelector selector)
    {
        _selector = selector;
    }

    public string Id => "pcsc";

    public string DisplayName => "USB reader (ACR122U, ACR1552U, PC/SC)";

    public bool IsSupported
    {
        get
        {
            try
            {
                using var context = ContextFactory.Instance.Establish(SCardScope.System);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool ShowsSystemPrompt => false;

    public bool SupportsReaderSelection => true;

    public Task<IReadOnlyList<NfcReaderInfo>> GetReadersAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<NfcReaderInfo>>(() =>
        {
            try
            {
                using var context = ContextFactory.Instance.Establish(SCardScope.System);
                var names = context.GetReaders() ?? Array.Empty<string>();
                return names
                    .Where(name => !name.Contains(" SAM", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(name => name.Contains("PICC", StringComparison.OrdinalIgnoreCase))
                    .Select(name => new NfcReaderInfo(name, name))
                    .ToList();
            }
            catch (PCSCException)
            {
                return [];
            }
        }, cancellationToken);
    }

    public async Task<INfcTagConnection> ConnectAsync(NfcConnectOptions options, CancellationToken cancellationToken = default)
    {
        var readerName = options.ReaderId;
        if (string.IsNullOrEmpty(readerName))
        {
            var readers = await GetReadersAsync(cancellationToken);
            readerName = readers.FirstOrDefault()?.Id ?? throw new NfcException("No USB NFC reader was found.");
        }

        var context = ContextFactory.Instance.Establish(SCardScope.System);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ICardReader reader;
                try
                {
                    reader = await Task.Run(() => context.ConnectReader(readerName, SCardShareMode.Shared, SCardProtocol.Any), cancellationToken);
                }
                catch (PCSCException ex) when (IsNoCard(ex))
                {
                    await Task.Delay(300, cancellationToken);
                    continue;
                }

                var card = new PcscCard(reader);
                try
                {
                    var strategy = await Task.Run(() => _selector.Select(readerName, card), cancellationToken);
                    var uid = await Task.Run(card.GetUid, cancellationToken);
                    return new PcscTagConnection(context, card, strategy, uid);
                }
                catch (NfcException) when (!IsCardPresent(context, readerName))
                {
                    // The tag left (or was never really there) while working out how to talk to it: keep waiting.
                    System.Diagnostics.Debug.WriteLine($"PC/SC: no tag on {readerName} any more; waiting.");
                    card.Dispose();
                    await Task.Delay(300, cancellationToken);
                }
                catch
                {
                    card.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static bool IsCardPresent(ISCardContext context, string readerName)
    {
        try
        {
            return context.GetReaderStatus(readerName).EventState.HasFlag(SCRState.Present);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNoCard(PCSCException ex) =>
        ex.SCardError is SCardError.NoSmartcard or SCardError.RemovedCard or SCardError.UnpoweredCard or SCardError.UnresponsiveCard or SCardError.ResetCard;
}

/// <summary>
/// A connected card plus helpers for pseudo APDUs.
/// </summary>
public sealed class PcscCard : IDisposable
{
    private readonly ICardReader _reader;

    public PcscCard(ICardReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// When set, every APDU and response is recorded (used while working out how to talk to a reader).
    /// </summary>
    public List<string>? Log { get; set; }

    /// <summary>
    /// Sends an APDU and returns the full response including SW1 SW2.
    /// </summary>
    public byte[] Transmit(byte[] apdu)
    {
        try
        {
            var sendPci = SCardPCI.GetPci(_reader.Protocol);
            var receivePci = new SCardPCI();
            var receiveBuffer = new byte[512];
            var received = _reader.Transmit(sendPci, apdu, apdu.Length, receivePci, receiveBuffer, receiveBuffer.Length);
            var response = receiveBuffer.AsSpan(0, received).ToArray();
            Log?.Add($"> {AmiiTag.Tags.Hex.ToSpacedHex(apdu)}  < {AmiiTag.Tags.Hex.ToSpacedHex(response)}");
            System.Diagnostics.Debug.WriteLine($"PC/SC > {AmiiTag.Tags.Hex.ToSpacedHex(apdu)}  < {AmiiTag.Tags.Hex.ToSpacedHex(response)}");
            return response;
        }
        catch (Exception ex)
        {
            Log?.Add($"> {AmiiTag.Tags.Hex.ToSpacedHex(apdu)}  ! {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"PC/SC > {AmiiTag.Tags.Hex.ToSpacedHex(apdu)}  ! {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Resets the card connection so the reader reactivates the tag (clears a halted tag or a leftover session).
    /// </summary>
    public void ResetCard()
    {
        try
        {
            _reader.Reconnect(SCardShareMode.Shared, SCardProtocol.Any, SCardReaderDisposition.Reset);
            Log?.Add("(card reset)");
        }
        catch (Exception ex)
        {
            Log?.Add($"(card reset failed: {ex.Message})");
        }
    }

    /// <summary>
    /// Sends an APDU and returns the data, throwing if SW1 SW2 isn't 90 00.
    /// </summary>
    public byte[] TransmitExpectOk(byte[] apdu)
    {
        var response = Transmit(apdu);
        if (!IsOk(response))
        {
            throw new NfcException($"Reader returned status {(response.Length >= 2 ? $"{response[^2]:X2}{response[^1]:X2}" : "(none)")}.");
        }

        return response.AsSpan(0, response.Length - 2).ToArray();
    }

    public static bool IsOk(byte[] response) => response.Length >= 2 && response[^2] == 0x90 && response[^1] == 0x00;

    public byte[] GetUid()
    {
        try
        {
            return TransmitExpectOk([0xFF, 0xCA, 0x00, 0x00, 0x00]);
        }
        catch (NfcException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        try
        {
            _reader.Dispose();
        }
        catch
        {
        }
    }
}

internal sealed class PcscTagConnection : INfcTagConnection
{
    private readonly ISCardContext _context;
    private readonly PcscCard _card;
    private readonly IPcscTransceiveStrategy _strategy;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _closed;

    public PcscTagConnection(ISCardContext context, PcscCard card, IPcscTransceiveStrategy strategy, byte[] uid)
    {
        _context = context;
        _card = card;
        _strategy = strategy;
        Uid = uid;
    }

    public byte[] Uid { get; }

    public int MaxFastReadPages => _strategy.MaxFastReadPages;

    public bool IsDirectCommandMode => _strategy.IsDirect;

    public async Task<byte[]> TransceiveAsync(byte[] command, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => _strategy.Transceive(_card, command), cancellationToken);
        }
        catch (PCSCException ex)
        {
            throw new NfcException("The tag was removed or the reader stopped responding.", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void SetStatus(string message)
    {
    }

    public Task CloseAsync(string? errorMessage = null)
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        _closed = true;
        try
        {
            _strategy.End(_card);
        }
        catch
        {
        }

        _card.Dispose();
        _context.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();
}
