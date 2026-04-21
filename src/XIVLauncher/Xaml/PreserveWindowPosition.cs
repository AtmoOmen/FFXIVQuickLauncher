using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace XIVLauncher.Xaml;

public static class PreserveWindowPosition
{
    private const int SW_SHOWNORMAL    = 1;
    private const int SW_SHOWMINIMIZED = 2;

    public static void RestorePosition(Window window)
    {
        if (App.Settings.MainWindowPlacement == null)
            return;

        var placement = App.Settings.MainWindowPlacement.Value;
        placement.length  = Marshal.SizeOf<WindowPlacement>();
        placement.flags   = 0;
        placement.showCmd = placement.showCmd == SW_SHOWMINIMIZED ? SW_SHOWNORMAL : placement.showCmd;

        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPlacement(hwnd, ref placement);
    }

    public static void SaveWindowPosition(Window window)
    {
        WindowPlacement wp;
        var             hwnd = new WindowInteropHelper(window).Handle;
        GetWindowPlacement(hwnd, out wp);
        App.Settings.MainWindowPlacement = wp;
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, [In] ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, out WindowPlacement lpwndpl);

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    (
        int x,
        int y
    )
    {
        public int X = x;
        public int Y = y;
    }

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    (
        int left,
        int top,
        int right,
        int bottom
    )
    {
        public int Left   = left;
        public int Top    = top;
        public int Right  = right;
        public int Bottom = bottom;
    }

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPlacement
    {
        public int   length;
        public int   flags;
        public int   showCmd;
        public Point minPosition;
        public Point maxPosition;
        public Rect  normalPosition;
    }
}
