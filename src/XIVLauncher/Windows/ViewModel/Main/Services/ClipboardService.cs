using System.Runtime.InteropServices;
using Serilog;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

/// <summary>
///     剪贴板写入服务, 剪贴板被占用时按固定间隔重试
/// </summary>
public class ClipboardService
{
    private const int  MAX_ATTEMPTS   = 40;
    private const int  RETRY_DELAY_MS = 50;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVABLE   = 0x0002;

    public bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
        {
            if (TrySetTextOnce(text))
                return true;

            Thread.Sleep(RETRY_DELAY_MS);
        }

        Log.Warning("复制账号信息到剪贴板失败: 剪贴板持续被占用");
        return false;
    }

    private static bool TrySetTextOnce(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            // CF_UNICODETEXT 要求 GMEM_MOVABLE 全局内存, 含结尾 null 字符
            var byteCount = (text.Length + 1) * 2;
            var hGlobal   = GlobalAlloc(GMEM_MOVABLE, (UIntPtr)byteCount);
            if (hGlobal == IntPtr.Zero)
                return false;

            var target = GlobalLock(hGlobal);

            if (target == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
            {
                // 设置失败时系统未接管内存, 需自行释放
                GlobalFree(hGlobal);
                return false;
            }

            // 设置成功后系统接管 hGlobal, 不可再释放
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
