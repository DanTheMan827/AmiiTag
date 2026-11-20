using AmiiTag.Nfc;
using AmiiTag.Nfc.Flipper;

namespace AmiiTag.NfcRelay;

/// <summary>
/// Answers each frame the Flipper relays with the real tag's answer, through the serial reader, and notices when the
/// real tag goes away (<see cref="Lost"/>).
/// </summary>
/// <remarks>
/// <see cref="Respond"/> runs on the Flipper transport's read thread (the bridge expects an answer synchronously); the
/// serial round trip blocks that thread, which only delays the next frame. <see cref="CheckPresenceAsync"/> runs on
/// the main loop; both share the reader through a gate.
/// </remarks>
internal sealed class TagRelay(INfcTagConnection tag, TimeSpan timeout, RelayLog log)
{
    // No presence check while frames are flowing: they show the tag is there.
    private static readonly TimeSpan QuietBeforeCheck = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _reselect;
    private long _lastFrameTicks = Environment.TickCount64;

    public INfcTagConnection Tag => tag;

    /// <summary>
    /// Completes (with the reason) when the real tag has left the reader.
    /// </summary>
    public Task<string> Lost => _lost.Task;

    /// <summary>
    /// Reselects the real tag before the next frame (after a field off, HLTA, NAK or error, which leave the emulated
    /// tag idle; the real one would otherwise still be selected, and authenticated, from before).
    /// </summary>
    public void ResetRealTag() => _reselect = true;

    public BridgeAnswer Respond(BridgeFrame frame)
    {
        Interlocked.Exchange(ref _lastFrameTicks, Environment.TickCount64);
        if (!frame.IsStandard || _lost.Task.IsCompleted)
        {
            // Bad CRC or a partial byte: there's nothing valid to pass on.
            return BridgeAnswer.Silence;
        }

        if (!_gate.Wait(timeout))
        {
            return BridgeAnswer.Silence;
        }

        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            if (_reselect)
            {
                _reselect = false;
                if (tag is INfcReselectable { CanReselect: true } reselectable)
                {
                    reselectable.ReselectAsync(deadline.Token).GetAwaiter().GetResult();
                }
            }

            var answer = tag.TransceiveAsync(frame.Data, deadline.Token).GetAwaiter().GetResult();
            switch (answer.Length)
            {
                case 0:
                    return BridgeAnswer.Silence;
                case 1 when answer[0] == 0x0A:
                    return BridgeAnswer.Nibble(0x0A);
                case 1 when answer[0] <= 0x0F:
                    // A NAK: the tag goes idle, on both sides.
                    _reselect = true;
                    return BridgeAnswer.NibbleThenIdle(answer[0]);
                default:
                    return BridgeAnswer.Frame(answer);
            }
        }
        catch (NfcNakException nak)
        {
            // Some drivers (the PN532's) report a NAK as an exception: it's still the tag's answer, so pass it on.
            _reselect = true;
            return BridgeAnswer.NibbleThenIdle(nak.Nak);
        }
        catch (Exception ex)
        {
            _reselect = true;
            if (ex is NfcConnectionLostException)
            {
                // The reader lost the tag (or found a different one): the main loop stops the emulation.
                _lost.TrySetResult(ex.Message);
            }
            else
            {
                log.Error(ex is OperationCanceledException
                    ? $"The real tag didn't answer within {timeout.TotalMilliseconds:F0} ms"
                    : $"Real tag: {ex.Message}");
            }

            return BridgeAnswer.Silence;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// While the Flipper's reader is quiet, checks the real tag is still on the serial reader: a READ of page 0 (which
    /// changes nothing on an active tag), else a reselect. Neither answering means it's gone.
    /// </summary>
    public async Task CheckPresenceAsync(CancellationToken cancellationToken)
    {
        if (_lost.Task.IsCompleted ||
            Environment.TickCount64 - Interlocked.Read(ref _lastFrameTicks) < QuietBeforeCheck.TotalMilliseconds ||
            !await _gate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            if (!_reselect)
            {
                try
                {
                    var page0 = await tag.TransceiveAsync([0x30, 0x00], deadline.Token);
                    if (page0.Length >= 4)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                }
            }

            // Idle (after a NAK or HLTA) or not answering: wake it. That throws when no tag, or another one, answers.
            if (tag is INfcReselectable { CanReselect: true } reselectable)
            {
                using var reselectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                reselectDeadline.CancelAfter(timeout);
                await reselectable.ReselectAsync(reselectDeadline.Token);
                _reselect = false;
            }
            else
            {
                _lost.TrySetResult("The tag stopped answering.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _lost.TrySetResult(ex is OperationCanceledException ? "The tag stopped answering." : ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}
