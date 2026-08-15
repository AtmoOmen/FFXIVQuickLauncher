using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace XIVLauncher.Xaml.Components;

/// <summary>
///     Base window that replaces the default OS title bar with an immersive
///     WindowChrome-based frame. It keeps native behaviors (Aero Snap, resize,
///     minimize/maximize animations and the DWM drop shadow) while letting the
///     content draw its own caption bar.
/// </summary>
public class ChromeWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode   = 20;
    private const int DwmwaWindowCornerPreference = 33;

    private const int DwmwcpRound = 2;

    private readonly WindowChrome chrome;

    public ChromeWindow()
    {
        WindowStyle = WindowStyle.None;

        chrome = new WindowChrome
        {
            CaptionHeight = 28,
            ResizeBorderThickness = new Thickness(6, 4, 6, 6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };

        WindowChrome.SetWindowChrome(this, chrome);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        ConfigureChromeForResizeMode();
        base.OnSourceInitialized(e);
        ApplyImmersiveChrome();
    }

    private void ConfigureChromeForResizeMode()
    {
        if (ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            // 32px title bar = 4px top resize handle + 28px caption.
            chrome.CaptionHeight = 28;
            chrome.ResizeBorderThickness = new Thickness(6, 4, 6, 6);
        }
        else
        {
            // Fixed-size windows don't need resize handles; make the entire
            // 32px title bar act as the caption.
            chrome.CaptionHeight = 32;
            chrome.ResizeBorderThickness = new Thickness(0);
        }
    }

    private void ApplyImmersiveChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // Keep the system context menu and any remaining frame bits dark so they
        // match the launcher's dark theme.
        var useDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));

        // Request rounded corners on Windows 11. The call is a harmless no-op on
        // older systems, so no OS version check is required.
        var cornerPreference = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
