using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GameCapture.Engine;

public sealed record MonitorInfo(IntPtr Handle, string DeviceName, int Width, int Height, bool IsPrimary);

/// <summary>
/// Keeps a Windows.Graphics.Capture session running against one monitor and always holds
/// the most recent frame. <see cref="TakeLatestFrame"/> hands ownership of that frame to the
/// caller (who must dispose it); the capture loop keeps refilling in the background.
/// </summary>
public sealed class MonitorCapture : IDisposable
{
    private const int BufferCount = 2;
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private readonly Lock _gate = new();
    private Direct3D11CaptureFrame? _latestFrame;
    private SizeInt32 _poolSize;
    private bool _disposed;

    public bool BorderDisabled { get; }
    public SizeInt32 ContentSize => _item.Size;

    public MonitorCapture(IntPtr hmonitor)
    {
        _device = CaptureInterop.CreateDirect3DDevice();
        _item = CaptureInterop.CreateItemForMonitor(hmonitor);
        _poolSize = _item.Size;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, BufferCount, _poolSize);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { /* older OS builds: cosmetic only */ }

        // Removing the yellow border needs Borderless capture access; the request may be
        // denied for unpackaged apps, in which case the border stays (cosmetic only).
        try
        {
            GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask().Wait(TimeSpan.FromSeconds(2));
            _session.IsBorderRequired = false;
            BorderDisabled = true;
        }
        catch
        {
            BorderDisabled = false;
        }

        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var frame = sender.TryGetNextFrame();
        if (frame is null)
            return;

        // Monitor resolution changed (e.g. game switched res): resize the pool.
        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            _poolSize = frame.ContentSize;
            sender.Recreate(_device, PixelFormat, BufferCount, _poolSize);
        }

        Direct3D11CaptureFrame? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }
            previous = _latestFrame;
            _latestFrame = frame;
        }
        previous?.Dispose();
    }

    /// <summary>Returns the most recent frame (caller owns/disposes it), or null if none arrived yet.</summary>
    public Direct3D11CaptureFrame? TakeLatestFrame()
    {
        lock (_gate)
        {
            var frame = _latestFrame;
            _latestFrame = null;
            return frame;
        }
    }

    public void Dispose()
    {
        Direct3D11CaptureFrame? pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            pending = _latestFrame;
            _latestFrame = null;
        }

        pending?.Dispose();
        _session.Dispose();
        _framePool.FrameArrived -= OnFrameArrived;
        _framePool.Dispose();
        _device.Dispose();
    }

    // ---- Monitor enumeration -------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public uint Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    private const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>All monitors, primary first — so config monitorIndex 0 always means the primary.</summary>
    public static List<MonitorInfo> EnumerateMonitors()
    {
        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEXW { Size = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
                if (GetMonitorInfoW(hMonitor, ref info))
                {
                    monitors.Add(new MonitorInfo(
                        hMonitor,
                        info.DeviceName,
                        info.Monitor.Right - info.Monitor.Left,
                        info.Monitor.Bottom - info.Monitor.Top,
                        (info.Flags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            },
            IntPtr.Zero);

        return monitors.OrderByDescending(m => m.IsPrimary).ToList();
    }
}
