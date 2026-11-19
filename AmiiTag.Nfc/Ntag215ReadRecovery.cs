namespace AmiiTag.Nfc;

/// <summary>
/// Bounded recovery for the NTAG215 read-only path. A reselect can reset a v3 tag's active sector/authentication,
/// so sector-aware v3 operations and writes must retain their own recovery policies.
/// </summary>
internal static class Ntag215ReadRecovery
{
    internal const int Attempts = 3;

    internal static async Task<T> RunAsync<T>(INfcTagConnection connection, Func<Task<T>> read, CancellationToken token)
    {
        for (var attempt = 1; ; ++attempt)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await read();
            }
            catch (NfcException ex) when (ex is not NfcCommandNotSupportedException and not NfcConnectionLostException &&
                attempt < Attempts && connection is INfcReselectable { CanReselect: true })
            {
                System.Diagnostics.Debug.WriteLine($"NTAG215 read: {ex.Message} (attempt {attempt}); reselecting.");
                await Task.Delay(100 * attempt, token);
                // If reselect fails or a different tag is detected, stop. Never retry the read in an unknown session.
                await ((INfcReselectable)connection).ReselectAsync(token);
            }
        }
    }
}
