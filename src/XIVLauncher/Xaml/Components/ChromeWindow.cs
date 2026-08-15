using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace XIVLauncher.Xaml.Components;

/// <summary>
///     Base window that removes the visible OS frame while keeping the native
///     caption style. Windows therefore still plays its own show/hide/minimize/
///     maximize animation (no custom, flash-prone opacity animation is needed),
///     and the window keeps Aero Snap, native resizing, the DWM drop shadow and
///     Windows 11 rounded corners.
/// </summary>
public class ChromeWindow : Window
{
    private const int WmNcCalcSize    = 0x0083;
    private const int WmNcHitTest     = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;

    private const int HtClient      = 1;
    private const int HtCaption     = 2;
    private const int HtLeft        = 10;
    private const int HtRight       = 11;
    private const int HtTop         = 12;
    private const int HtTopLeft     = 13;
    private const int HtTopRight    = 14;
    private const int HtBottom      = 15;
    private const int HtBottomLeft  = 16;
    private const int HtBottomRight = 17;

    private const int DwmwaUseImmersiveDarkMode   = 20;
    private const int DwmwaWindowCornerPreference = 33;

    private const int DwmwcpRound = 2;

    private const uint MonitorDefaultToNearest = 0x00000002;

    // Must match the TitleBar control height.
    private const double TitleBarHeight        = 32;
    private const double ResizeBorderThickness = 6;
    private const double TopResizeThickness    = 4;

    private HwndSource? hwndSource;

    public ChromeWindow()
    {
        // Intentionally keep the default SingleBorderWindow style. Its WS_CAPTION
        // flag is what makes Windows animate the window on show/close/minimize.
        // WM_NCCALCSIZE below hides the frame visually.
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        ApplyImmersiveChrome();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcCalcSize when wParam != IntPtr.Zero:
                // Make the client area cover the whole window so the OS does not
                // paint a title bar, while WS_CAPTION keeps the native animation.
                handled = true;
                return IntPtr.Zero;

            case WmGetMinMaxInfo:
                FixMaximizedBounds(lParam);
                handled = true;
                return IntPtr.Zero;

            case WmNcHitTest:
                var hitTest = HitTestNonClient(lParam);
                if (hitTest != 0)
                {
                    handled = true;
                    return new IntPtr(hitTest);
                }

                break;
        }

        return IntPtr.Zero;
    }

    private int HitTestNonClient(IntPtr lParam)
    {
        var screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        var screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var point   = PointFromScreen(new Point(screenX, screenY));

        // The caption buttons are marked as chrome-visible so they stay in the
        // client area and keep receiving clicks.
        if (InputHitTest(point) is { } element && WindowChrome.GetIsHitTestVisibleInChrome(element))
            return HtClient;

        var canResize = ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
                        && WindowState != WindowState.Maximized;

        var topBorder     = canResize ? TopResizeThickness : 0;
        var captionHeight = TitleBarHeight - topBorder;
        var leftBorder    = canResize ? ResizeBorderThickness : 0;
        var rightBorder   = canResize ? ResizeBorderThickness : 0;
        var bottomBorder  = canResize ? ResizeBorderThickness : 0;

        var row = 1;
        var col = 1;
        var onResizeBorder = false;

        if (point.Y >= 0 && point.Y < topBorder + captionHeight)
        {
            onResizeBorder = point.Y < topBorder;
            row            = 0;
        }
        else if (point.Y >= ActualHeight - bottomBorder)
        {
            row = 2;
        }

        if (point.X >= 0 && point.X < leftBorder)
            col = 0;
        else if (point.X >= ActualWidth - rightBorder)
            col = 2;

        // Below the top resize strip, the far-left/far-right caption edges behave
        // as left/right resize handles instead of diagonal corners.
        if (row == 0 && col != 1 && !onResizeBorder)
            row = 1;

        if (row == 0 && col == 1)
            return onResizeBorder ? HtTop : HtCaption;

        return (row, col) switch
        {
            (0, 0) => HtTopLeft,
            (0, 2) => HtTopRight,
            (1, 0) => HtLeft,
            (1, 2) => HtRight,
            (2, 0) => HtBottomLeft,
            (2, 1) => HtBottom,
            (2, 2) => HtBottomRight,
            _      => HtClient,
        };
    }

    private void FixMaximizedBounds(IntPtr lParam)
    {
        if (WindowState != WindowState.Normal)
            return;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        // The frame is hidden, so make the maximized window exactly fill the work
        // area instead of spilling its borders off-screen (which would crop the
        // client area).
        mmi.ptMaxPosition.X = monitorInfo.rcWork.Left - monitorInfo.rcMonitor.Left;
        mmi.ptMaxPosition.Y = monitorInfo.rcWork.Top  - monitorInfo.rcMonitor.Top;
        mmi.ptMaxSize.X     = monitorInfo.rcWork.Right  - monitorInfo.rcWork.Left;
        mmi.ptMaxSize.Y     = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;

        Marshal.StructureToPtr(mmi, lParam, false);
    }

    private void ApplyImmersiveChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // Keep the system context menu and frame dark to match the launcher theme.
        var useDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));

        // Request rounded corners on Windows 11; harmless no-op on older systems.
        var cornerPreference = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo monitorInfo);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public WinPoint ptReserved;
        public WinPoint ptMaxSize;
        public WinPoint ptMaxPosition;
        public WinPoint ptMinTrackSize;
        public WinPoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int     cbSize;
        public WinRect rcMonitor;
        public WinRect rcWork;
        public uint    dwFlags;
    }
}
