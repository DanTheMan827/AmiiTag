#if DEBUG
using System.Diagnostics;
using System.Text;

namespace AmiiTag.Maui.Diagnostics;

/// <summary>
/// Debug builds: everything written with Debug.WriteLine/Trace (which includes ILogger output through the Debug
/// provider, and the PC/SC, MFRC522 and NFC traffic) plus unhandled exceptions, saved to AmiiTag.log so it can be
/// inspected after a run. The file is cleared on every launch. Where it goes, most inspectable first:
/// <list type="bullet">
/// <item>Windows, Mac: next to the executable (falls back to the app data folder if that isn't writable).</item>
/// <item>iOS: the app's Documents folder, visible in the Files app.</item>
/// <item>Android: the app's external files folder (Android/data/&lt;package&gt;/files), reachable over USB or adb.</item>
/// </list>
/// </summary>
internal static class DebugFileLog
{
    public const string FileName = "AmiiTag.log";

    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static string? LogPath { get; private set; }

    public static void Start()
    {
        if (_writer != null)
        {
            return;
        }

        foreach (var folder in CandidateFolders())
        {
            LogPath = Open(Path.Combine(folder, FileName));
            if (LogPath != null)
            {
                break;
            }
        }

        if (LogPath == null)
        {
            return;
        }

        Trace.Listeners.Add(new FileTraceListener());
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteLine("FATAL", $"Unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => WriteLine("ERROR", $"Unobserved task exception: {e.Exception}");

        WriteLine("INFO", $"AmiiTag {AppInfo.Current.VersionString} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}; {DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString} on {DeviceInfo.Current.Model}; log: {LogPath}");
    }

    private static IEnumerable<string> CandidateFolders()
    {
#if ANDROID
        if (Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath is { } external)
        {
            yield return external;
        }
#elif IOS
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#else
        yield return AppContext.BaseDirectory;
#endif
        yield return FileSystem.AppDataDirectory;
    }

    public static void WriteLine(string level, string? message)
    {
        lock (Gate)
        {
            _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
        }
    }

    private static void WriteRaw(string? text)
    {
        lock (Gate)
        {
            _writer?.Write(text);
        }
    }

    private static string? Open(string path)
    {
        try
        {
            // FileMode.Create truncates: each launch starts a fresh log.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            return path;
        }
        catch
        {
            return null;
        }
    }

    private sealed class FileTraceListener : TraceListener
    {
        private bool _midLine;

        public override void Write(string? message)
        {
            if (!_midLine)
            {
                WriteRaw($"{DateTime.Now:HH:mm:ss.fff} [DEBUG] ");
                _midLine = true;
            }

            WriteRaw(message);
        }

        public override void WriteLine(string? message)
        {
            if (_midLine)
            {
                WriteRaw(message + Environment.NewLine);
                _midLine = false;
            }
            else
            {
                DebugFileLog.WriteLine("DEBUG", message);
            }
        }
    }
}
#endif
