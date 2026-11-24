#if MACCATALYST
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AmiiTag.Nfc;
using CoreFoundation;

namespace AmiiTag.Maui.NfcProviders;

/// <summary>
/// macOS / Mac Catalyst serial ports via IOKit and POSIX termios, for MFRC522 and PN532 readers
/// on USB serial adapters (driven directly in UART mode).
/// </summary>
/// <remarks>
/// Ports of a Flipper Zero are left out: probing its USB serial (CLI) port with MFRC522 commands crashes it.
/// </remarks>
public sealed class SystemSerialPortFactory : ISerialPortFactory
{
    private const int FlipperVendorId = 0x0483;
    private const int FlipperProductId = 0x5740;

    public bool IsSupported => true;

    public Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default)
    {
        var excluded = FlipperPorts();
        var portPaths = Directory.Exists("/dev")
            ? Directory.EnumerateFiles("/dev", "cu.*")
            : Array.Empty<string>();

        IReadOnlyList<NfcReaderInfo> ports = portPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path =>
                !excluded.Contains(path) &&
                !path.Contains("Bluetooth-Incoming-Port", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("debug-console", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains("wlan-debug", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new NfcReaderInfo(path, path.StartsWith("/dev/cu.", StringComparison.OrdinalIgnoreCase) ? path[8..] : path))
            .ToList();

        return Task.FromResult(ports);
    }

    private static HashSet<string> FlipperPorts()
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists("/dev"))
        {
            foreach (var path in Directory.EnumerateFiles("/dev", "cu.usbmodemflip_*"))
            {
                ports.Add(path);
            }
        }

        try
        {
            var matching = IOServiceMatching("IOSerialBSDClient");
            if (matching == IntPtr.Zero)
                return ports;

            if (IOServiceGetMatchingServices(0, matching, out var iterator) != 0 || iterator == 0)
                return ports;

            try
            {
                uint service;
                while ((service = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        var callout = GetStringProperty(service, "IOCalloutDevice");
                        if (string.IsNullOrEmpty(callout))
                            continue;

                        var vid = GetParentIntProperty(service, "idVendor");
                        var pid = GetParentIntProperty(service, "idProduct");
                        if (vid == FlipperVendorId && pid == FlipperProductId)
                        {
                            ports.Add(callout);
                        }
                    }
                    finally
                    {
                        IOObjectRelease(service);
                    }
                }
            }
            finally
            {
                IOObjectRelease(iterator);
            }
        }
        catch
        {
            // Fall back to the /dev/cu.usbmodemflip_* check above if IOKit is restricted.
        }

        return ports;
    }

    public Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default)
    {
        if (FlipperPorts().Contains(portId))
        {
            throw new NfcException($"{portId} is a Flipper Zero, not a supported NFC serial reader.");
        }

        // O_RDWR (0x0002) | O_NOCTTY (0x20000) | O_NONBLOCK (0x0004)
        var fd = PosixOpen(portId, 0x0002 | 0x20000 | 0x0004);
        if (fd < 0)
        {
            var err = Marshal.GetLastPInvokeError();
            throw new NfcException($"Couldn't open {portId} (errno {err}).");
        }

        try
        {
            // Clear O_NONBLOCK now that open() succeeded without waiting for carrier detect
            var flags = PosixFcntl(fd, 3 /* F_GETFL */, 0);
            if (flags >= 0)
                PosixFcntl(fd, 4 /* F_SETFL */, flags & ~0x0004);

            ConfigurePort(fd, baudRate);
        }
        catch (Exception ex)
        {
            PosixClose(fd);
            throw new NfcException($"Couldn't configure {portId}: {ex.Message}", ex);
        }

        return Task.FromResult<ISerialPortStream>(new PosixSerialPortStream(fd));
    }

    private static unsafe void ConfigurePort(int fd, int baudRate)
    {
        var tio = default(Termios);
        if (PosixTcGetAttr(fd, &tio) != 0)
            throw new IOException($"tcgetattr failed (errno {Marshal.GetLastPInvokeError()}).");

        // Raw 8N1 mode, local connection, enable receiver, disable hardware/software flow control
        PosixCfMakeRaw(&tio);
        tio.c_cflag |= 0x00000800 /* CLOCAL */ | 0x00000080 /* CREAD */ | 0x00000300 /* CS8 */;
        tio.c_cflag &= ~(nuint)(0x00001000 /* PARENB */ | 0x00000400 /* CSTOPB */ | 0x00030000 /* CCTS_OFLOW | CRTS_IFLOW */);
        tio.c_iflag &= ~(nuint)(0x00000200 /* IXON */ | 0x00000400 /* IXOFF */ | 0x00000800 /* IXANY */);


        // VMIN = 1, VTIME = 0 (blocking read returns as soon as >= 1 byte arrives)
        tio.c_cc[16 /* VMIN */] = 1;
        tio.c_cc[17 /* VTIME */] = 0;

        // On macOS, speed_t is the numeric baud rate directly (e.g., 115200)
        PosixCfSetISpeed(&tio, (nuint)baudRate);
        PosixCfSetOSpeed(&tio, (nuint)baudRate);

        if (PosixTcSetAttr(fd, 0 /* TCSANOW */, &tio) != 0)
            throw new IOException($"tcsetattr failed (errno {Marshal.GetLastPInvokeError()}).");

        // Assert DTR and RTS
        int modemBits = 0x0002 /* TIOCM_DTR */ | 0x0004 /* TIOCM_RTS */;
        PosixIoctl(fd, 0x8004746d /* TIOCMBIS */, &modemBits);
        PosixTcFlush(fd, 3 /* TCIOFLUSH */);
    }

        private sealed class PosixSerialPortStream : ISerialPortStream
    {
        private readonly int _fd;
        private readonly Channel<byte[]> _received = Channel.CreateUnbounded<byte[]>();
        private readonly Thread _readerThread;
        private volatile bool _closed;
        private byte[]? _pending;
        private int _pendingOffset;

        public PosixSerialPortStream(int fd)
        {
            _fd = fd;
            _readerThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "macOS Serial Reader"
            };
            _readerThread.Start();
        }

        private unsafe void ReadLoop()
        {
            var buffer = new byte[256];
            var pfd = new PollFd { fd = _fd, events = 0x0001 /* POLLIN */ };

            try
            {
                fixed (byte* ptr = buffer)
                {
                    while (!_closed)
                    {
                        pfd.revents = 0;
                        // Wait up to 50ms for data so we never block indefinitely inside read()
                        var ready = PosixPoll(&pfd, 1, 50);
                        if (_closed)
                            break;

                        if (ready < 0)
                        {
                            // EINTR (4) is retryable; any other error means port disconnected
                            if (Marshal.GetLastPInvokeError() == 4)
                                continue;
                            break;
                        }

                        if (ready == 0)
                            continue; // Timeout, loop around and re-check _closed

                        // POLLERR (0x0008) | POLLHUP (0x0010) | POLLNVAL (0x0020)
                        if ((pfd.revents & (0x0008 | 0x0010 | 0x0020)) != 0)
                            break;

                        if ((pfd.revents & 0x0001 /* POLLIN */) != 0)
                        {
                            var read = PosixRead(_fd, (IntPtr)ptr, (nuint)buffer.Length);
                            if (read <= 0)
                                break;

                            _received.Writer.TryWrite(buffer.AsSpan(0, (int)read).ToArray());
                        }
                    }
                }
            }
            catch
            {
                // Port closed or unplugged.
            }
            finally
            {
                _received.Writer.TryComplete();
            }
        }

        public unsafe Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellationToken.ThrowIfCancellationRequested();

            fixed (byte* ptr = data)
            {
                var written = 0;
                while (written < data.Length)
                {
                    var n = PosixWrite(_fd, (IntPtr)(ptr + written), (nuint)(data.Length - written));
                    if (n <= 0)
                        throw new IOException($"Serial write failed (errno {Marshal.GetLastPInvokeError()}).");
                    written += (int)n;
                }
            }

            return Task.CompletedTask;
        }

        public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
        {
            if (_pending == null)
            {
                try
                {
                    _pending = await _received.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    throw new IOException("The serial port was closed.");
                }

                _pendingOffset = 0;
            }

            var take = Math.Min(count, _pending.Length - _pendingOffset);
            Array.Copy(_pending, _pendingOffset, buffer, offset, take);
            _pendingOffset += take;
            if (_pendingOffset >= _pending.Length)
                _pending = null;

            return take;
        }

        public async ValueTask DisposeAsync()
        {
            if (_closed)
                return;

            _closed = true;
            _received.Writer.TryComplete();

            // Wait off the UI thread for ReadLoop to exit its 50ms poll() before calling close(_fd),
            // otherwise macOS AppleUSBCDCACMData blocks close() while a read() is active.
            await Task.Run(() =>
            {
                _readerThread.Join(200);
                PosixTcFlush(_fd, 3 /* TCIOFLUSH */);
                PosixClose(_fd);
            }).ConfigureAwait(false);
        }
    }

    #region IOKit, CoreFoundation & POSIX P/Invokes (macOS arm64/x64)

    private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Termios
    {
        public nuint c_iflag;
        public nuint c_oflag;
        public nuint c_cflag;
        public nuint c_lflag;
        public fixed byte c_cc[20];
        public nuint c_ispeed;
        public nuint c_ospeed;
    }

private static string? GetStringProperty(uint entry, string key)
{
using var cfKey = new CFString(key);
var prop = IORegistryEntryCreateCFProperty(entry, cfKey.Handle, IntPtr.Zero, 0);
if (prop == IntPtr.Zero)
return null;
try
{
return CFGetTypeID(prop) == CFStringGetTypeID() ? CFString.FromHandle(prop) : null;
}
finally
{
CFRelease(prop);
}
}
private static int? GetParentIntProperty(uint entry, string key)
{
using var cfKey = new CFString(key);
var prop = IORegistryEntrySearchCFProperty(entry, "IOService", cfKey.Handle, IntPtr.Zero, 3);
if (prop == IntPtr.Zero)
return null;
try
{
if (CFGetTypeID(prop) != CFNumberGetTypeID())
return null;
return CFNumberGetValue(prop, (nint)3, out var value) ? value : null;
}
finally
{
CFRelease(prop);
}
}
[DllImport(IOKitLib)]
private static extern IntPtr IOServiceMatching(string name);
[DllImport(IOKitLib)]
private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint existing);
[DllImport(IOKitLib)]
private static extern uint IOIteratorNext(uint iterator);
[DllImport(IOKitLib)]
private static extern int IOObjectRelease(uint obj);
[DllImport(IOKitLib)]
private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
[DllImport(IOKitLib)]
private static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, string plane, IntPtr key, IntPtr allocator, uint options);
[DllImport(CoreFoundationLib)]
private static extern nuint CFGetTypeID(IntPtr cf);
[DllImport(CoreFoundationLib)]
private static extern nuint CFNumberGetTypeID();
[DllImport(CoreFoundationLib)]
private static extern nuint CFStringGetTypeID();
[DllImport(CoreFoundationLib)]
[return: MarshalAs(UnmanagedType.U1)]
private static extern bool CFNumberGetValue(IntPtr number, nint theType, out int valuePtr);
[DllImport(CoreFoundationLib)]
private static extern void CFRelease(IntPtr cf);
[DllImport(LibSystem, EntryPoint = "open", SetLastError = true)]
private static extern int PosixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);
[DllImport(LibSystem, EntryPoint = "close", SetLastError = true)]
private static extern int PosixClose(int fd);
[DllImport(LibSystem, EntryPoint = "read", SetLastError = true)]
private static extern nint PosixRead(int fd, IntPtr buf, nuint count);
[StructLayout(LayoutKind.Sequential)]
private struct PollFd
{
    public int fd;
    public short events;
    public short revents;
}

[DllImport(LibSystem, EntryPoint = "poll", SetLastError = true)]
private static extern unsafe int PosixPoll(PollFd* fds, uint nfds, int timeout);

[DllImport(LibSystem, EntryPoint = "write", SetLastError = true)]
private static extern nint PosixWrite(int fd, IntPtr buf, nuint count);
[DllImport(LibSystem, EntryPoint = "fcntl", SetLastError = true)]
private static extern int PosixFcntl(int fd, int cmd, int arg);
[DllImport(LibSystem, EntryPoint = "ioctl", SetLastError = true)]
private static extern unsafe int PosixIoctl(int fd, nuint request, int* argp);
[DllImport(LibSystem, EntryPoint = "tcgetattr", SetLastError = true)]
private static extern unsafe int PosixTcGetAttr(int fd, Termios* termios);
[DllImport(LibSystem, EntryPoint = "tcsetattr", SetLastError = true)]
private static extern unsafe int PosixTcSetAttr(int fd, int optionalActions, Termios* termios);
[DllImport(LibSystem, EntryPoint = "cfmakeraw")]
private static extern unsafe void PosixCfMakeRaw(Termios* termios);
[DllImport(LibSystem, EntryPoint = "cfsetispeed", SetLastError = true)]
private static extern unsafe int PosixCfSetISpeed(Termios* termios, nuint speed);
[DllImport(LibSystem, EntryPoint = "cfsetospeed", SetLastError = true)]
private static extern unsafe int PosixCfSetOSpeed(Termios* termios, nuint speed);
[DllImport(LibSystem, EntryPoint = "tcflush", SetLastError = true)]
private static extern int PosixTcFlush(int fd, int queueSelector);
[DllImport(LibSystem, EntryPoint = "tcdrain", SetLastError = true)]
private static extern int PosixTcDrain(int fd);
#endregion
}
#endif