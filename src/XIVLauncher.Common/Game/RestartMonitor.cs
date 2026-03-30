using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace XIVLauncher.Common.Game;

public class RestartMonitor
{
    private const int RESTART_EXIT_CODE = 0x12345678;
    private static readonly TimeSpan RestartedProcessExitTimeout      = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestartedProcessExitPollInterval = TimeSpan.FromMilliseconds(200);

    public async Task MonitorAsync
    (
        FFXIVProcess              gameProcess,
        Func<Task<FFXIVProcess?>> restartProcessAsync,
        CancellationToken         cancellationToken = default
    )
    {
        var process     = gameProcess.UnderlyingProcess;
        var processName = gameProcess.ProcessName;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processHandle = IntPtr.Zero;

            try
            {
                var currentProcess = Process.GetCurrentProcess();

                if (!DuplicateHandle(currentProcess.Handle, process.Handle, currentProcess.Handle, out processHandle, 0, false, 2))
                {
                    Log.Error("DuplicateHandle failed: {LastError}", Marshal.GetLastWin32Error());
                    processHandle = process.Handle;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to duplicate process handle");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            int exitCode;

            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                if (processHandle != IntPtr.Zero && GetExitCodeProcess(processHandle, out var nativeExitCode))
                    exitCode = (int)nativeExitCode;
                else
                {
                    Log.Error("无法获取进程退出码: {LastError}", Marshal.GetLastWin32Error());
                    if (processHandle != IntPtr.Zero && processHandle != process.Handle)
                        CloseHandle(processHandle);
                    break;
                }
            }
            finally
            {
                if (processHandle != IntPtr.Zero && processHandle != process.Handle)
                    CloseHandle(processHandle);
            }

            if (exitCode != RESTART_EXIT_CODE)
            {
                Log.Information("游戏进程正常退出, 退出码: 0x{ExitCode:X}", exitCode);
                break;
            }

            Log.Information("游戏进程请求重启并退出, 退出码: 0x{ExitCode:X}, 等待重启后进程", exitCode);

            using var newProcess = await FindNewGameProcess(processName, DateTime.Now.AddSeconds(-30), cancellationToken).ConfigureAwait(false);

            if (newProcess == null)
            {
                Log.Error("无法找到重启后游戏进程");
                break;
            }

            Log.Information("找到重启后游戏进程 {ProcessId}，正在终止并重新启动以进行注入...", newProcess.ProcessID);

            try
            {
                newProcess.UnderlyingProcess.Kill();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "无法发送终止请求到重启后的游戏进程");
                break;
            }

            if (!await WaitForProcessExitAsync(newProcess.ProcessID, processName, cancellationToken).ConfigureAwait(false))
            {
                Log.Error
                (
                    "重启后的游戏进程 {ProcessId} 在 {TimeoutSeconds} 秒内仍未退出",
                    newProcess.ProcessID,
                    RestartedProcessExitTimeout.TotalSeconds
                );
                break;
            }

            Log.Information("正在重新启动游戏...");

            using var restartedProcess = await restartProcessAsync().ConfigureAwait(false);
            if (restartedProcess == null)
                Log.Error("重启游戏失败");

            break;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle
    (
        IntPtr                               hSourceProcessHandle,
        IntPtr                               hSourceHandle,
        IntPtr                               hTargetProcessHandle,
        out IntPtr                           lpTargetHandle,
        uint                                 dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint                                 dwOptions
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private async Task<FFXIVProcess?> FindNewGameProcess(string processName, DateTime afterTime, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 60; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            var processes = Process.GetProcessesByName(processName);

            foreach (var process in processes)
            {
                try
                {
                    if (process.StartTime > afterTime)
                        return new FFXIVProcess(process);
                }
                catch
                {
                    // Ignore errors accessing StartTime
                }

                process.Dispose();
            }
        }

        return null;
    }

    private async Task<bool> WaitForProcessExitAsync(int processId, string processName, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)RestartedProcessExitTimeout.TotalMilliseconds;

        while (ProcessExists(processId, processName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Environment.TickCount64 >= deadline)
                return false;

            await Task.Delay(RestartedProcessExitPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private static bool ProcessExists(int processId, string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.Id == processId)
                    return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}
