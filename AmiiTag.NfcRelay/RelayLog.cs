using AmiiTag.Nfc.Flipper;

namespace AmiiTag.NfcRelay;

/// <summary>
/// The traffic, to the console and optionally a file, in the same layout as the app's emulation log.
/// </summary>
internal sealed class RelayLog : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly Lock _lock = new();

    public RelayLog(string? path)
    {
        if (path != null)
        {
            _file = new StreamWriter(path, append: false) { AutoFlush = true };
        }
    }

    public void Header() => WriteLine("Time         Flipper ms    Data                                     Meaning");

    public void Info(string text) => Write(EmulationLogKind.Info, text);

    public void Error(string text) => Write(EmulationLogKind.Error, text);

    public void Write(EmulationLogKind kind, string text, byte[]? data = null, uint micros = 0) =>
        WriteLine(new EmulationLogEntry(DateTimeOffset.Now, micros, kind, data ?? [], text).Format());

    public void Exchange(BridgeFrame frame, BridgeAnswer? answer, TimeSpan hostTime)
    {
        var readerText = frame.IsStandard ? NfcCommandNames.Describe(frame.Data) : $"{frame.BitCount} bits, no valid CRC";
        Write(EmulationLogKind.Reader, readerText, frame.Data, frame.TimestampMicros);
        if (frame.FromTable)
        {
            Write(EmulationLogKind.Tag, "answered by the Flipper (answer table)");
            return;
        }

        if (answer is not { } a)
        {
            Write(EmulationLogKind.Error, "Relay failed; stayed silent");
            return;
        }

        var what = a.Bits switch
        {
            0 => "(silent)",
            4 => a.Data is [0x0A] ? "ACK" : $"NAK {a.Data[0]:X}",
            _ => $"{a.Data.Length} bytes",
        };
        var idle = a.Flags.HasFlag(BridgeTxFlags.Sleep) ? ", then idle" : string.Empty;
        Write(EmulationLogKind.Tag, $"{what}{idle}  [{hostTime.TotalMilliseconds:F1} ms]", a.Bits >= 8 ? a.Data : null);
    }

    public void TagEvent(BridgeMessageType type, uint micros)
    {
        var text = type switch
        {
            BridgeMessageType.FieldOn => "Field on",
            BridgeMessageType.FieldOff => "Field off",
            BridgeMessageType.Activated => "Selected (anticollision done by the Flipper)",
            BridgeMessageType.Halted => "HLTA",
            _ => type.ToString(),
        };
        Write(EmulationLogKind.FieldOff, text, null, micros);
    }

    private void WriteLine(string line)
    {
        lock (_lock)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public void Dispose() => _file?.Dispose();
}
