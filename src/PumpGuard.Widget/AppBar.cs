using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PumpGuard.Widget;

/// <summary>
/// Registers a window as a Windows "application desktop toolbar" (like the taskbar): the shell reserves
/// a strip along a screen edge for it, so maximized windows no longer cover it. Primary monitor only.
/// </summary>
public sealed class AppBar
{
    private const uint AbmNew = 0, AbmRemove = 1, AbmQueryPos = 2, AbmSetPos = 3;
    private const uint AbeLeft = 0, AbeTop = 1, AbeRight = 2, AbeBottom = 3;
    private const int AbnPosChanged = 1;

    private readonly Window _window;
    private readonly Action _onPositionChanged;
    private uint _callbackMessage;
    private bool _registered;

    public AppBar(Window window, Action onPositionChanged)
    {
        _window = window;
        _onPositionChanged = onPositionChanged;
    }

    public bool IsRegistered => _registered;

    /// <summary>
    /// Reserves a strip <paramref name="thickness"/> DIPs thick along <paramref name="edge"/>: full screen width
    /// for top/bottom, the work-area height (clear of the taskbar) for left/right. Returns the strip in DIPs.
    /// </summary>
    public Rect Dock(BarEdge edge, double thickness)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = hwnd };
        if (!_registered)
        {
            _callbackMessage = RegisterWindowMessage("PumpGuardAppBar");
            data.uCallbackMessage = _callbackMessage;
            SHAppBarMessage(AbmNew, ref data);
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
            _registered = true;
        }

        var dpi = VisualTreeHelper.GetDpi(_window);
        var vertical = edge is BarEdge.Left or BarEdge.Right;
        int screenW = GetSystemMetrics(0), screenH = GetSystemMetrics(1);
        int t = (int)Math.Ceiling(thickness * (vertical ? dpi.DpiScaleX : dpi.DpiScaleY));
        var work = SystemParameters.WorkArea;
        int workTop = (int)(work.Top * dpi.DpiScaleY), workBottom = (int)(work.Bottom * dpi.DpiScaleY);

        data.uEdge = edge switch { BarEdge.Left => AbeLeft, BarEdge.Right => AbeRight, BarEdge.Bottom => AbeBottom, _ => AbeTop };
        data.rc = edge switch
        {
            BarEdge.Left => new RectI(0, workTop, t, workBottom),
            BarEdge.Right => new RectI(screenW - t, workTop, screenW, workBottom),
            BarEdge.Bottom => new RectI(0, screenH - t, screenW, screenH),
            _ => new RectI(0, 0, screenW, t),
        };
        SHAppBarMessage(AbmQueryPos, ref data); // the shell moves rc clear of the taskbar and other bars
        switch (edge)
        {
            case BarEdge.Left: data.rc.Right = data.rc.Left + t; break;
            case BarEdge.Right: data.rc.Left = data.rc.Right - t; break;
            case BarEdge.Bottom: data.rc.Top = data.rc.Bottom - t; break;
            default: data.rc.Bottom = data.rc.Top + t; break;
        }
        SHAppBarMessage(AbmSetPos, ref data);

        return new Rect(data.rc.Left / dpi.DpiScaleX, data.rc.Top / dpi.DpiScaleY,
            (data.rc.Right - data.rc.Left) / dpi.DpiScaleX, (data.rc.Bottom - data.rc.Top) / dpi.DpiScaleY);
    }

    public void Undock()
    {
        if (!_registered) return;
        var hwnd = new WindowInteropHelper(_window).Handle;
        var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>(), hWnd = hwnd };
        SHAppBarMessage(AbmRemove, ref data);
        HwndSource.FromHwnd(hwnd)?.RemoveHook(Hook);
        _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Taskbar moved/resized, resolution changed, another app bar appeared: take our place again.
        if (_registered && msg == _callbackMessage && wParam.ToInt32() == AbnPosChanged)
            _window.Dispatcher.BeginInvoke(_onPositionChanged);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectI(int left, int top, int right, int bottom)
    {
        public int Left = left, Top = top, Right = right, Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RectI rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
