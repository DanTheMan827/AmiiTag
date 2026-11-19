#if WINDOWS
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AmiiTag.Nfc.Flipper;
using Microsoft.Win32.SafeHandles;

namespace AmiiTag.Maui.Services;

/// <summary>
/// The NFC Bridge's bulk interface through WinUSB, which Windows binds by itself (the Flipper app carries Microsoft OS
/// 2.0 descriptors). Many packets per millisecond instead of HID's one, so large answers reach the Flipper in time.
/// </summary>
public sealed class WinUsbFlipperTransportProvider : IFlipperTransportProvider, IDisposable
{
    /// <summary>
    /// BRIDGE_WINUSB_GUID in flipper/nfc_bridge/bridge_protocol.h.
    /// </summary>
    private static readonly Guid InterfaceGuid = new("C4A1D7E2-5B3F-4E88-9D21-6F0A3B7C9E14");

    private readonly Timer _poll;
    private volatile bool _present;

    public WinUsbFlipperTransportProvider()
    {
        _present = FindDevicePath() != null;

        // Device interface arrival has no managed event; a cheap enumeration every couple of seconds does.
        _poll = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public bool IsSupported => true;

    public bool IsDevicePresent => _present;

    public event EventHandler? DevicesChanged;

    private void Refresh()
    {
        var present = FindDevicePath() != null;
        if (present != _present)
        {
            _present = present;
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task<IBridgeTransport?> OpenAsync(CancellationToken cancellationToken = default) => Task.Run<IBridgeTransport?>(() =>
        FindDevicePath() is { } path ? WinUsbBridgeTransport.TryOpen(path) : null, cancellationToken);

    public void Dispose() => _poll.Dispose();

    private static string? FindDevicePath()
    {
        var guid = InterfaceGuid;
        var set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == Native.InvalidHandle)
        {
            return null;
        }

        try
        {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data))
            {
                return null;
            }

            Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
            if (required == 0)
            {
                return null;
            }

            var detail = Marshal.AllocHGlobal((int)required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is that of the fixed part (DWORD + one WCHAR, padded).
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                return Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero)
                    ? Marshal.PtrToStringUni(detail + 4)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private sealed class WinUsbBridgeTransport : IBridgeTransport, IBridgeReportSource
    {
        private const byte PipeOut = 0x02;
        private const byte PipeIn = 0x82;

        private readonly SafeFileHandle _file;
        private readonly IntPtr _usb;
        private readonly Channel<byte[]> _reports = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly Lock _writeLock = new();
        private volatile bool _closed;
        private Action<byte[]>? _onReport;
        private Action<Exception?>? _onClosed;
        private int _started;

        private WinUsbBridgeTransport(SafeFileHandle file, IntPtr usb)
        {
            _file = file;
            _usb = usb;

            uint writeTimeout = 1000;
            Native.WinUsb_SetPipePolicy(_usb, PipeOut, Native.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref writeTimeout);
            Native.WinUsb_FlushPipe(_usb, PipeIn);
        }

        public void StartReports(Action<byte[]> onReport, Action<Exception?> onClosed)
        {
            _onReport = onReport;
            _onClosed = onClosed;
            EnsureStarted();
        }

        private void EnsureStarted()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                new Thread(ReadLoop) { IsBackground = true, Name = "Flipper WinUSB", Priority = ThreadPriority.Highest }.Start();
            }
        }

        public string Name => "USB bulk";

        public static WinUsbBridgeTransport? TryOpen(string path)
        {
            var file = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_ATTRIBUTE_NORMAL | Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (file.IsInvalid)
            {
                file.Dispose();
                return null;
            }

            if (!Native.WinUsb_Initialize(file, out var usb))
            {
                file.Dispose();
                return null;
            }

            return new WinUsbBridgeTransport(file, usb);
        }

        private void ReadLoop()
        {
            var buffer = new byte[BridgeProtocol.ReportSize];
            try
            {
                while (!_closed)
                {
                    if (!Native.WinUsb_ReadPipe(_usb, PipeIn, buffer, (uint)buffer.Length, out var read, IntPtr.Zero))
                    {
                        if (_closed)
                        {
                            break;
                        }

                        throw new IOException($"Reading from the Flipper failed (error {Marshal.GetLastWin32Error()}).");
                    }

                    if (read == BridgeProtocol.ReportSize)
                    {
                        var report = buffer.ToArray();
                        if (_onReport is { } onReport)
                        {
                            onReport(report);
                        }
                        else
                        {
                            _reports.Writer.TryWrite(report);
                        }
                    }
                }

                Close(null);
            }
            catch (Exception ex)
            {
                Close(_closed ? null : ex);
            }
        }

        private void Close(Exception? error)
        {
            _reports.Writer.TryComplete(error);
            _onClosed?.Invoke(error);
        }

        public void Write(ReadOnlySpan<byte> reports)
        {
            // One transfer for the whole message: its packets leave back to back.
            var data = reports.ToArray();
            lock (_writeLock)
            {
                if (!Native.WinUsb_WritePipe(_usb, PipeOut, data, (uint)data.Length, out var written, IntPtr.Zero) || written != data.Length)
                {
                    throw new IOException($"Writing to the Flipper failed (error {Marshal.GetLastWin32Error()}).");
                }
            }
        }

        public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken)
        {
            EnsureStarted();
            return _reports.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                Native.WinUsb_AbortPipe(_usb, PipeIn);
                Native.WinUsb_AbortPipe(_usb, PipeOut);
                Native.WinUsb_Free(_usb);
                _file.Dispose();
                _reports.Writer.TryComplete();
                if (Volatile.Read(ref _started) == 0)
                {
                    _onClosed?.Invoke(null);
                }
            }

            return ValueTask.CompletedTask;
        }
    }

    private static class Native
    {
        public const uint DIGCF_PRESENT = 0x02;
        public const uint DIGCF_DEVICEINTERFACE = 0x10;
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x01;
        public const uint FILE_SHARE_WRITE = 0x02;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint PIPE_TRANSFER_TIMEOUT = 0x03;
        public static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, byte[] buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);

        [DllImport("winusb.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_FlushPipe(IntPtr interfaceHandle, byte pipeId);
    }
}
#endif
