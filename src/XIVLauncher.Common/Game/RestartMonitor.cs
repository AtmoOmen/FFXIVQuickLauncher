using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Serilog;

namespace XIVLauncher.Common.Game;

public class RestartMonitor
{
    private const           int      RESTART_EXIT_CODE                = 0x12345678;
    private const           string   DALAMUD_CRASH_HANDLER_NAME       = "DalamudCrashHandler";
    private const           string   DALAMUD_INJECTOR_PROCESS_NAME    = "Dalamud.Injector.exe";
    private static readonly TimeSpan CrashHandlerProcessLookback      = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CrashHandlerRestartTimeout       = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CrashHandlerExitGracePeriod      = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartedProcessExitTimeout      = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestartedProcessExitPollInterval = TimeSpan.FromMilliseconds(200);

    public readonly record struct RestartOptions
    (
        bool ForceNoDalamud,
        bool NoThirdPlugins,
        bool NoPlugins
    )
    {
        public static RestartOptions Normal => default;
    }

    public async Task MonitorAsync
    (
        FFXIVProcess                              gameProcess,
        RestartOptions                            defaultRestartOptions,
        Func<RestartOptions, Task<FFXIVProcess?>> restartProcessAsync,
        CancellationToken                         cancellationToken = default
    )
    {
        var process                       = gameProcess.UnderlyingProcess;
        var processName                   = gameProcess.ProcessName;
        var crashHandlerObservationTime   = TryGetProcessStartTime(process)?.Subtract(CrashHandlerProcessLookback) ?? DateTime.Now.Subtract(CrashHandlerProcessLookback);
        var relatedCrashHandlerProcessIds = GetProcessIdsStartedAfter(DALAMUD_CRASH_HANDLER_NAME, crashHandlerObservationTime);

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

            if (exitCode == RESTART_EXIT_CODE)
            {
                Log.Information("游戏进程请求重启并退出, 退出码: 0x{ExitCode:X}, 等待重启后进程", exitCode);
                await RestartFromNewGameProcessAsync
                (
                    processName,
                    DateTime.Now.AddSeconds(-30),
                    TimeSpan.FromSeconds(60),
                    defaultRestartOptions,
                    restartProcessAsync,
                    cancellationToken
                ).ConfigureAwait(false);
                break;
            }

            if (exitCode != 0 && relatedCrashHandlerProcessIds.Count > 0)
            {
                Log.Information
                (
                    "游戏进程以非零退出码退出, 检测到本次会话存在 Dalamud 故障处理器, 将等待其是否拉起新的游戏进程。退出码: 0x{ExitCode:X}",
                    exitCode
                );

                if (await TryRestartFromCrashHandlerAsync
                        (
                            processName,
                            relatedCrashHandlerProcessIds,
                            defaultRestartOptions,
                            restartProcessAsync,
                            cancellationToken
                        )
                        .ConfigureAwait(false))
                    break;
            }

            if (exitCode != 0)
            {
                Log.Information("游戏进程退出, 未检测到需要由启动器接管的重启流程, 退出码: 0x{ExitCode:X}", exitCode);
                break;
            }

            Log.Information("游戏进程正常退出, 退出码: 0x{ExitCode:X}", exitCode);
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

    private async Task<bool> TryRestartFromCrashHandlerAsync
    (
        string                                    processName,
        IReadOnlySet<int>                         relatedCrashHandlerProcessIds,
        RestartOptions                            defaultRestartOptions,
        Func<RestartOptions, Task<FFXIVProcess?>> restartProcessAsync,
        CancellationToken                         cancellationToken
    )
    {
        var             deadline               = DateTime.Now + CrashHandlerRestartTimeout;
        var             exitGraceEnds          = DateTime.Now + CrashHandlerExitGracePeriod;
        var             restartObservationTime = DateTime.Now.AddSeconds(-1);
        RestartOptions? detectedRestartOptions = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            detectedRestartOptions ??= TryFindCrashHandlerRestartOptions(restartObservationTime);

            using var newProcess = TryFindNewGameProcess(processName, DateTime.Now.AddSeconds(-30));

            if (newProcess != null)
            {
                var effectiveRestartOptions = detectedRestartOptions ?? InferRestartOptions(newProcess, defaultRestartOptions);

                Log.Information
                (
                    "Dalamud 故障处理器已拉起新的游戏进程, 将由启动器接管后续重启。模式: 禁用 Dalamud = {ForceNoDalamud}, 禁用第三方插件 = {NoThirdPlugins}, 禁用全部插件 = {NoPlugins}",
                    effectiveRestartOptions.ForceNoDalamud,
                    effectiveRestartOptions.NoThirdPlugins,
                    effectiveRestartOptions.NoPlugins
                );

                return await RestartFromNewGameProcessAsync
                           (
                               processName,
                               DateTime.Now.AddSeconds(-30),
                               TimeSpan.FromSeconds(1),
                               effectiveRestartOptions,
                               restartProcessAsync,
                               cancellationToken,
                               newProcess
                           )
                           .ConfigureAwait(false);
            }

            if (ProcessExists(relatedCrashHandlerProcessIds, DALAMUD_CRASH_HANDLER_NAME))
                exitGraceEnds = DateTime.Now + CrashHandlerExitGracePeriod;
            else if (DateTime.Now >= deadline)
                return false;
            else if (DateTime.Now >= exitGraceEnds)
                return false;

            await Task.Delay(RestartedProcessExitPollInterval, cancellationToken).ConfigureAwait(false);
        }

    }

    private async Task<bool> RestartFromNewGameProcessAsync
    (
        string                                    processName,
        DateTime                                  afterTime,
        TimeSpan                                  findTimeout,
        RestartOptions                            restartOptions,
        Func<RestartOptions, Task<FFXIVProcess?>> restartProcessAsync,
        CancellationToken                         cancellationToken,
        FFXIVProcess?                             newProcess = null
    )
    {
        using var ownedNewProcess = newProcess == null
                                        ? await FindNewGameProcess(processName, afterTime, findTimeout, cancellationToken).ConfigureAwait(false)
                                        : null;
        var effectiveNewProcess = newProcess ?? ownedNewProcess;

        if (effectiveNewProcess == null)
        {
            Log.Error("无法找到重启后游戏进程");
            return false;
        }

        Log.Information("找到重启后游戏进程 {ProcessId}，正在终止并重新启动以进行注入...", effectiveNewProcess.ProcessID);

        try
        {
            effectiveNewProcess.UnderlyingProcess.Kill();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法发送终止请求到重启后的游戏进程");
            return false;
        }

        if (!await WaitForProcessExitAsync(effectiveNewProcess.ProcessID, processName, cancellationToken).ConfigureAwait(false))
        {
            Log.Error
            (
                "重启后的游戏进程 {ProcessId} 在 {TimeoutSeconds} 秒内仍未退出",
                effectiveNewProcess.ProcessID,
                RestartedProcessExitTimeout.TotalSeconds
            );
            return false;
        }

        Log.Information("正在重新启动游戏...");

        using var restartedProcess = await restartProcessAsync(restartOptions).ConfigureAwait(false);

        if (restartedProcess == null)
        {
            Log.Error("重启游戏失败");
            return false;
        }

        return true;
    }

    private async Task<FFXIVProcess?> FindNewGameProcess
    (
        string            processName,
        DateTime          afterTime,
        TimeSpan          timeout,
        CancellationToken cancellationToken
    )
    {
        var deadline = DateTime.Now + timeout;

        while (DateTime.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = TryFindNewGameProcess(processName, afterTime);
            if (process != null)
                return process;

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static FFXIVProcess? TryFindNewGameProcess(string processName, DateTime afterTime)
    {
        foreach (var process in Process.GetProcessesByName(processName))
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

        return null;
    }

    private static HashSet<int> GetProcessIdsStartedAfter(string processName, DateTime afterTime)
    {
        HashSet<int> processIds = [];

        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.StartTime > afterTime)
                    processIds.Add(process.Id);
            }
            catch
            {
                // Ignore errors accessing StartTime
            }
            finally
            {
                process.Dispose();
            }
        }

        return processIds;
    }

    private static DateTime? TryGetProcessStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private static RestartOptions? TryFindCrashHandlerRestartOptions(DateTime afterTime)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher
            (
                "SELECT ProcessId, CommandLine, CreationDate FROM Win32_Process " + $"WHERE Name = '{DALAMUD_INJECTOR_PROCESS_NAME}'"
            );

            DateTime?       latestStartTime = null;
            RestartOptions? restartOptions  = null;

            foreach (var o in searcher.Get())
            {
                var process = (ManagementObject)o;

                using (process)
                {
                    if (process["CommandLine"] is not string commandLine || !IsDalamudLaunchCommand(commandLine))
                        continue;

                    if (process["CreationDate"] is not string creationDate)
                        continue;

                    var startTime = ManagementDateTimeConverter.ToDateTime(creationDate);
                    if (startTime <= afterTime || latestStartTime >= startTime)
                        continue;

                    latestStartTime = startTime;
                    restartOptions  = ParseRestartOptions(commandLine);
                }
            }

            return restartOptions;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取 Dalamud 注入器命令行失败, 将回退为基于进程状态的重启模式推断");
            return null;
        }
    }

    private static bool IsDalamudLaunchCommand(string commandLine) =>
        commandLine.Contains($" {DALAMUD_LAUNCH_ARGUMENT}", StringComparison.OrdinalIgnoreCase);

    private static RestartOptions ParseRestartOptions(string commandLine)
    {
        if (commandLine.Contains(DALAMUD_WITHOUT_ARGUMENT, StringComparison.OrdinalIgnoreCase))
            return new RestartOptions(true, false, false);

        if (commandLine.Contains(DALAMUD_NO_PLUGIN_ARGUMENT, StringComparison.OrdinalIgnoreCase))
            return new RestartOptions(false, false, true);

        if (commandLine.Contains(DALAMUD_NO_THIRD_PARTY_ARGUMENT, StringComparison.OrdinalIgnoreCase))
            return new RestartOptions(false, true, false);

        return RestartOptions.Normal;
    }

    private static RestartOptions InferRestartOptions(FFXIVProcess process, RestartOptions defaultRestartOptions)
    {
        if (!process.HasInjected)
            return new RestartOptions(true, false, false);

        if (defaultRestartOptions != RestartOptions.Normal)
        {
            Log.Warning
            (
                "未能从 Dalamud 注入器命令行恢复重启模式, 将回退为本次启动的原始模式。禁用 Dalamud = {ForceNoDalamud}, 禁用第三方插件 = {NoThirdPlugins}, 禁用全部插件 = {NoPlugins}",
                defaultRestartOptions.ForceNoDalamud,
                defaultRestartOptions.NoThirdPlugins,
                defaultRestartOptions.NoPlugins
            );
            return defaultRestartOptions;
        }

        Log.Warning("未能从 Dalamud 注入器命令行恢复重启模式, 将回退为正常重启");
        return RestartOptions.Normal;
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

    private static bool ProcessExists(IReadOnlySet<int> processIds, string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (processIds.Contains(process.Id))
                    return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private const string DALAMUD_LAUNCH_ARGUMENT = "launch";
    private const string DALAMUD_WITHOUT_ARGUMENT = "--without-dalamud";
    private const string DALAMUD_NO_PLUGIN_ARGUMENT = "--no-plugin";
    private const string DALAMUD_NO_THIRD_PARTY_ARGUMENT = "--no-3rd-plugin";
}
