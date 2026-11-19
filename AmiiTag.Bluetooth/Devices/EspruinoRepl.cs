using System.Text;
using System.Text.Json;

namespace AmiiTag.Bluetooth.Devices;

/// <summary>
/// The Espruino JavaScript console over the Nordic UART service, used as a request/response channel: each expression
/// is sent as <c>\x10Bluetooth.println(JSON.stringify(expression))</c> (\x10 stops Espruino echoing the line) and its
/// reply is the next JSON line. One request at a time. Lines the firmware prints on its own with an <c>event</c> field
/// (a Flask's button presses...) are raised as <see cref="EventReceived"/> instead of being taken as a reply.
/// </summary>
public sealed class EspruinoRepl : IDisposable
{
    /// <summary>
    /// Events firmware sends unasked; any other JSON line answers the pending request (some commands' own replies are
    /// event objects too, such as delete-all's <c>{event:"deleteAllE"}</c>).
    /// </summary>
    private static readonly HashSet<string> UnsolicitedEvents =
        ["button", "delete", "refresh", "backupStart", "backup", "deleteAllEnd", "factoryResetEnd", "endFirmware"];

    private const int ChunkSize = 20;

    private readonly IBleCharacteristic _write;
    private readonly IBleCharacteristic _notify;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _sync = new();
    private readonly StringBuilder _buffer = new();
    private TaskCompletionSource<JsonElement?>? _pending;

    private EspruinoRepl(IBleCharacteristic write, IBleCharacteristic notify)
    {
        _write = write;
        _notify = notify;
        _notify.ValueUpdated += OnValueUpdated;
    }

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A line the firmware printed on its own, with an <c>event</c> field.
    /// </summary>
    public event EventHandler<JsonElement>? EventReceived;

    /// <summary>
    /// Opens the console on the connection's Nordic UART service, or returns null if it doesn't have one.
    /// </summary>
    public static async Task<EspruinoRepl?> OpenAsync(IBleConnection connection, CancellationToken cancellationToken = default)
    {
        IBleCharacteristic? write, notify;
        try
        {
            write = await connection.GetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartWrite, cancellationToken);
            notify = await connection.GetCharacteristicAsync(BleUuids.NordicUartService, BleUuids.NordicUartNotify, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        if (write == null || notify == null)
        {
            return null;
        }

        var repl = new EspruinoRepl(write, notify);
        await notify.StartNotificationsAsync(cancellationToken);
        return repl;
    }

    /// <summary>
    /// Ctrl-C: abandons whatever is on the console's input line (left there by other devices' probes, say).
    /// </summary>
    public async Task ClearInputLineAsync(CancellationToken cancellationToken = default)
    {
        await _write.WriteAsync([0x03, (byte)'\n'], true, cancellationToken);
        await Task.Delay(150, cancellationToken);
        lock (_sync)
        {
            _buffer.Clear();
        }
    }

    /// <summary>
    /// Evaluates a JavaScript expression on the device and returns its value as JSON (null for undefined).
    /// </summary>
    public async Task<JsonElement?> EvaluateAsync(string expression, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        await _requests.WaitAsync(cancellationToken);
        try
        {
            var pending = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending = pending;
            }

            var command = Encoding.Latin1.GetBytes($"\u0010Bluetooth.println(JSON.stringify({expression}));\n");
            for (var offset = 0; offset < command.Length; offset += ChunkSize)
            {
                await _write.WriteAsync(command[offset..Math.Min(command.Length, offset + ChunkSize)], true, cancellationToken);
            }

            try
            {
                return await pending.Task.WaitAsync(timeout ?? DefaultTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                // A dropped reply would otherwise be taken as the next request's: start over clean.
                lock (_sync)
                {
                    _buffer.Clear();
                }

                throw new TimeoutException("The device didn't answer.");
            }
        }
        finally
        {
            lock (_sync)
            {
                _pending = null;
            }

            _requests.Release();
        }
    }

    private void OnValueUpdated(object? sender, byte[] value)
    {
        var text = Encoding.Latin1.GetString(value);
        if (text == "<- Serial1\r\n>")
        {
            return;
        }

        var lines = new List<string>();
        lock (_sync)
        {
            _buffer.Append(text);
            while (true)
            {
                var content = _buffer.ToString();
                var newline = content.IndexOf('\n');
                if (newline < 0)
                {
                    break;
                }

                lines.Add(content[..newline]);
                _buffer.Remove(0, newline + 1);
            }
        }

        foreach (var line in lines)
        {
            HandleLine(line);
        }
    }

    private void HandleLine(string raw)
    {
        // Prompts, echoes and control characters around the JSON are dropped.
        var line = new string(raw.Where(c => c is >= ' ' and <= '~').ToArray()).Replace(">{", "{").Trim().TrimStart('>').Trim();
        if (line.Length == 0)
        {
            return;
        }

        JsonElement? value;
        if (line.StartsWith("undefined", StringComparison.Ordinal))
        {
            value = null;
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                value = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return;
            }
        }

        if (value is { ValueKind: JsonValueKind.Object } element && element.TryGetProperty("event", out var name) &&
            name.ValueKind == JsonValueKind.String && UnsolicitedEvents.Contains(name.GetString()!))
        {
            EventReceived?.Invoke(this, element);
            return;
        }

        TaskCompletionSource<JsonElement?>? pending;
        lock (_sync)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.TrySetResult(value);
    }

    /// <summary>
    /// A JavaScript string literal for text spliced into an expression (names and the like).
    /// </summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value);

    public void Dispose()
    {
        _notify.ValueUpdated -= OnValueUpdated;
        lock (_sync)
        {
            _pending?.TrySetCanceled();
            _pending = null;
        }
    }
}
