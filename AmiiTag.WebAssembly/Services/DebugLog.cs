#if DEBUG
using System.Diagnostics;
using System.Text;
using Microsoft.JSInterop;

namespace AmiiTag.WebAssembly.Services;

/// <summary>
/// Debug builds in the browser can't write a file, so everything written with Debug.WriteLine/Trace (Web Serial
/// MFRC522 traffic, BLE traffic, NFC errors, ILogger output) is kept in memory for this page load. Run
/// <c>amiitag.downloadDebugLog()</c> in the browser console to save it as AmiiTag.log. Reloading clears it.
/// It's also written to the browser console as it happens (without a debugger attached, Debug.WriteLine output
/// otherwise goes nowhere in the browser).
/// </summary>
public static class DebugLog
{
    private const int MaxLength = 4 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly StringBuilder Buffer = new();
    private static bool _started;

    public static void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Trace.Listeners.Add(new BufferListener());
        WriteLine("INFO", $"AmiiTag (web) started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    [JSInvokable("GetDebugLog")]
    public static string GetText()
    {
        lock (Gate)
        {
            return Buffer.ToString();
        }
    }

    private static void WriteLine(string level, string? message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        Console.WriteLine(line);
        Append(line + Environment.NewLine);
    }

    private static void Append(string text)
    {
        lock (Gate)
        {
            Buffer.Append(text);
            if (Buffer.Length > MaxLength)
            {
                Buffer.Remove(0, Buffer.Length - MaxLength);
            }
        }
    }

    private sealed class BufferListener : TraceListener
    {
        public override void Write(string? message)
        {
            Console.Write(message);
            Append(message ?? string.Empty);
        }

        public override void WriteLine(string? message) => DebugLog.WriteLine("DEBUG", message);
    }
}
#endif
