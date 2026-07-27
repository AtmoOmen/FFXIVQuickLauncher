using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using PInvoke;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Dalamud;

public class DalamudInjector : IDalamudRunner
{
    public static void Inject
    (
        FileInfo                    runner,
        int                         gamePid,
        IDictionary<string, string> environment,
        DalamudStartInfo            startInfo,
        bool                        safeMode       = false,
        bool                        noThirdPlugins = false,
        bool                        elevate        = false
    )
    {
        var launchArguments = new List<string>
        {
            "inject -v",
            $"{gamePid}",
            DalamudInjectorArgs.WorkingDirectory(startInfo.WorkingDirectory),
            DalamudInjectorArgs.ConfigurationPath(startInfo.ConfigurationPath),
            DalamudInjectorArgs.LoggingPath(startInfo.LoggingPath),
            DalamudInjectorArgs.PluginDirectory(startInfo.PluginDirectory),
            DalamudInjectorArgs.AssetDirectory(startInfo.AssetDirectory),
            DalamudInjectorArgs.ClientLanguage(4),
            DalamudInjectorArgs.DelayInitialize(startInfo.DelayInitializeMs),
            DalamudInjectorArgs.TsPackB64(Convert.ToBase64String(Encoding.UTF8.GetBytes(startInfo.TroubleshootingPackData))),
            DalamudInjectorArgs.LauncherDirectory(startInfo.LauncherDirectory)
        };

        if (safeMode) launchArguments.Add("--no-plugin");
        if (noThirdPlugins) launchArguments.Add(DalamudInjectorArgs.NO_THIRD_PARTY);

        launchArguments.Add(DalamudInjectorArgs.MANAGED_RESTART);

        var shouldElevate = elevate && !PlatformHelpers.IsElevated();
        var psi = new ProcessStartInfo(runner.FullName)
        {
            Arguments        = string.Join(" ", launchArguments),
            WorkingDirectory = runner.DirectoryName ?? Environment.CurrentDirectory,
            UseShellExecute  = shouldElevate,
            CreateNoWindow   = !shouldElevate,
            WindowStyle      = ProcessWindowStyle.Hidden
        };

        if (shouldElevate)
            psi.Verb = "runas";
        else
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;

            foreach (var keyValuePair in environment)
                psi.EnvironmentVariables[keyValuePair.Key] = keyValuePair.Value;
        }

        var previousEnvironment = new Dictionary<string, string?>();

        try
        {
            if (shouldElevate)
            {
                foreach (var keyValuePair in environment)
                {
                    previousEnvironment[keyValuePair.Key] = Environment.GetEnvironmentVariable(keyValuePair.Key);
                    Environment.SetEnvironmentVariable(keyValuePair.Key, keyValuePair.Value);
                }
            }

            using var dalamudProcess = Process.Start(psi) ?? throw new DalamudRunnerException("无法启动 Dalamud 注入器");
            var output = new ConcurrentQueue<string>();

            if (!shouldElevate)
            {
                dalamudProcess.OutputDataReceived += (_, args) => CaptureOutput(args.Data);
                dalamudProcess.ErrorDataReceived  += (_, args) => CaptureOutput(args.Data);
                dalamudProcess.BeginOutputReadLine();
                dalamudProcess.BeginErrorReadLine();
            }

            const int WAIT_INJECTOR_TIMEOUT_MS = 60 * 1000;
            if (!dalamudProcess.WaitForExit(WAIT_INJECTOR_TIMEOUT_MS))
            {
                dalamudProcess.Kill(true);
                throw new DalamudRunnerException("Dalamud 注入器运行超时");
            }

            dalamudProcess.WaitForExit();

            if (dalamudProcess.ExitCode != 0)
            {
                var exitCode = unchecked((uint)dalamudProcess.ExitCode);
                var details  = string.Join(Environment.NewLine, output);
                var message  = $"Dalamud 注入器失败, 退出码 0x{exitCode:X8}";

                if (!string.IsNullOrWhiteSpace(details))
                    message = $"{message}{Environment.NewLine}{details}";

                throw new DalamudRunnerException(message);
            }

            return;

            void CaptureOutput(string? line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;

                output.Enqueue(line);
                Log.Information("{InjectorOutput}", line);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
        {
            throw new DalamudRunnerException("已取消 Dalamud 注入器权限请求", ex);
        }
        finally
        {
            foreach (var keyValuePair in previousEnvironment)
                Environment.SetEnvironmentVariable(keyValuePair.Key, keyValuePair.Value);
        }
    }

    public unsafe Process? Run
    (
        FileInfo                    runner,
        bool                        fakeLogin,
        bool                        noPlugins,
        bool                        noThirdPlugins,
        FileInfo                    gameExe,
        string                      gameArgs,
        IDictionary<string, string> environment,
        DalamudLoadMethod           loadMethod,
        DalamudStartInfo            dalamudStartInfo
    )
    {
        var inheritableCurrentProcess = GetInheritableCurrentProcessHandle();

        if (gameExe == null)
            throw new ArgumentNullException(nameof(gameExe), "Game path was null");

        if (dalamudStartInfo == null)
            throw new ArgumentNullException(nameof(dalamudStartInfo), "StartInfo was null");

        if (dalamudStartInfo.TroubleshootingPackData == null)
            throw new ArgumentNullException(nameof(dalamudStartInfo.TroubleshootingPackData), "TS data was null");

        var launchArguments = new List<string>
        {
            DalamudInjectorArgs.LAUNCH,
            DalamudInjectorArgs.Mode(loadMethod == DalamudLoadMethod.EntryPoint ? "entrypoint" : "inject"),
            DalamudInjectorArgs.Game(gameExe.FullName),
            DalamudInjectorArgs.WorkingDirectory(dalamudStartInfo.WorkingDirectory),
            DalamudInjectorArgs.ConfigurationPath(dalamudStartInfo.ConfigurationPath),
            DalamudInjectorArgs.LoggingPath(dalamudStartInfo.LoggingPath),
            DalamudInjectorArgs.PluginDirectory(dalamudStartInfo.PluginDirectory),
            DalamudInjectorArgs.AssetDirectory(dalamudStartInfo.AssetDirectory),
            DalamudInjectorArgs.ClientLanguage(4),
            DalamudInjectorArgs.DelayInitialize(dalamudStartInfo.DelayInitializeMs),
            DalamudInjectorArgs.TsPackB64(Convert.ToBase64String(Encoding.UTF8.GetBytes(dalamudStartInfo.TroubleshootingPackData))),
            DalamudInjectorArgs.LauncherDirectory(dalamudStartInfo.LauncherDirectory)
        };

        if (inheritableCurrentProcess != null)
            launchArguments.Add(DalamudInjectorArgs.HandleOwner(inheritableCurrentProcess.Handle));

        if (loadMethod == DalamudLoadMethod.ACLonly)
            launchArguments.Add(DalamudInjectorArgs.WITHOUT_DALAMUD);

        if (fakeLogin)
            launchArguments.Add(DalamudInjectorArgs.FAKE_ARGUMENTS);

        if (noPlugins)
            launchArguments.Add(DalamudInjectorArgs.NO_PLUGIN);

        if (noThirdPlugins)
            launchArguments.Add(DalamudInjectorArgs.NO_THIRD_PARTY);

        // 始终启用托管重启: 崩溃处理器据此把重启决策编码进退出码, 由 RestartMonitor 接管
        launchArguments.Add(DalamudInjectorArgs.MANAGED_RESTART);

        launchArguments.Add("--");
        launchArguments.Add(gameArgs);

        var joinedArguments = string.Join(" ", launchArguments);
        var fullCommandLine = $"\"{runner.FullName}\" {joinedArguments}";
        var envVars         = SafeGetEnvVars();

        foreach (var keyValuePair in environment)
            envVars[keyValuePair.Key] = keyValuePair.Value;

        try
        {
            var environmentBlock = GetEnvironmentVariablesBlock(envVars);

            Log.Verbose
            (
                "Starting launch Dalamud with\n\tCmdLine: {CommandLine}\n\tEnvBlock: {EnvironmentBlock}",
                fullCommandLine,
                environmentBlock.Replace("\0", "\\0")
            );

            var                          kernelStartupInfo = Kernel32.STARTUPINFO.Create();
            Kernel32.PROCESS_INFORMATION kernelProcessInfo;

            var pipeSecAttr = Kernel32.SECURITY_ATTRIBUTES.Create();
            pipeSecAttr.bInheritHandle = 1;

            if (!Kernel32.CreatePipe(out var tempOutputHandle, out var childOutputPipeHandle, pipeSecAttr, 0))
                throw new System.ComponentModel.Win32Exception();

            Log.Verbose("=> Acquired pipe");

            var currentProcHandle = Kernel32.GetCurrentProcess();

            if (!DuplicateHandle(currentProcHandle.DangerousGetHandle(), tempOutputHandle.DangerousGetHandle(), currentProcHandle.DangerousGetHandle(), out var parentOutputPipeHandle, 0, false, DuplicateOptions.SameAccess))
                throw new System.ComponentModel.Win32Exception();

            Log.Verbose("=> Duplicated pipe handle");

            kernelStartupInfo.dwFlags    = Kernel32.StartupInfoFlags.STARTF_USESTDHANDLES;
            kernelStartupInfo.hStdOutput = childOutputPipeHandle.DangerousGetHandle();

            fixed (char* environmentBlockPtr = environmentBlock)
            {
                const Kernel32.CreateProcessFlags FLAGS = Kernel32.CreateProcessFlags.CREATE_NO_WINDOW | Kernel32.CreateProcessFlags.CREATE_UNICODE_ENVIRONMENT;

                var retVal = Kernel32.CreateProcess
                (
                    null,
                    fullCommandLine,
                    (Kernel32.SECURITY_ATTRIBUTES*)0,
                    (Kernel32.SECURITY_ATTRIBUTES*)0,
                    true,
                    FLAGS,
                    environmentBlockPtr,
                    Environment.CurrentDirectory,
                    ref kernelStartupInfo,
                    out kernelProcessInfo
                );

                if (!retVal)
                    throw new System.ComponentModel.Win32Exception();
            }

            Log.Verbose("=> Started process");

            if (kernelProcessInfo.hThread != IntPtr.Zero && kernelProcessInfo.hThread != new IntPtr(-1))
                Kernel32.CloseHandle(kernelProcessInfo.hThread);

            childOutputPipeHandle.Dispose();
            tempOutputHandle.Dispose();

            var       stdoutEncoding = new UTF8Encoding(false);
            using var injectorProcessHandle = new SafeProcessHandle(kernelProcessInfo.hProcess, true);
            using var stdoutStream   = new StreamReader(new FileStream(new SafeFileHandle(parentOutputPipeHandle, true), FileAccess.Read, 4096, false), stdoutEncoding, true, 4096);
            var       outputTask     = stdoutStream.ReadToEndAsync();

            const int WAIT_INJECTOR_TIMEOUT_MS = 60 * 1000;
            var       res                      = Kernel32.WaitForSingleObject(injectorProcessHandle, WAIT_INJECTOR_TIMEOUT_MS);

            if (res != Kernel32.WaitForSingleObjectResult.WAIT_OBJECT_0)
            {
                if (res == Kernel32.WaitForSingleObjectResult.WAIT_FAILED)
                    throw new System.ComponentModel.Win32Exception();

                throw new DalamudRunnerException("Injector did not exit in the expected timeout period");
            }

            Log.Verbose("=> WaitForSingleObject() complete");

            if (!Kernel32.GetExitCodeProcess(injectorProcessHandle.DangerousGetHandle(), out var exitCode))
                throw new System.ComponentModel.Win32Exception();

            if (exitCode != 0)
                throw new DalamudRunnerException($"Injector exit code was {exitCode}");

            var outputs = outputTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(outputs))
                throw new DalamudRunnerException("Injector output stream was empty");

            var output = outputs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(output))
                throw new DalamudRunnerException("No injector output");

            Log.Verbose("=> Reading result");

            Process gameProcess;

            try
            {
                Log.Verbose("=> Dalamud.Injector output: {Output}", output);
                var dalamudConsoleOutput = JsonConvert.DeserializeObject<DalamudConsoleOutput>(output);

                if (dalamudConsoleOutput == null)
                    throw new JsonReaderException("Unable to deserialize Dalamud console output");

                if (dalamudConsoleOutput.Handle == 0)
                {
                    Log.Warning($"=> Dalamud returned NULL process handle, attempting to recover by creating a new one from pid {dalamudConsoleOutput.Pid}...");
                    gameProcess = Process.GetProcessById(dalamudConsoleOutput.Pid);
                }
                else
                    gameProcess = new ExistingProcess((IntPtr)dalamudConsoleOutput.Handle);

                try
                {
                    Log.Verbose($"=> Got game process handle {gameProcess.Handle} with pid {gameProcess.Id}");
                }
                catch (InvalidOperationException ex)
                {
                    Log.Error(ex, $"=> Dalamud returned invalid process handle {gameProcess.Handle}, attempting to recover by creating a new one from pid {dalamudConsoleOutput.Pid}...");
                    gameProcess = Process.GetProcessById(dalamudConsoleOutput.Pid);
                    Log.Warning($"=> Recovered with process handle {gameProcess.Handle}");
                }

                if (gameProcess.Id != dalamudConsoleOutput.Pid)
                    Log.Warning($"=> Internal Process ID {gameProcess.Id} does not match Dalamud provided one {dalamudConsoleOutput.Pid}");
            }
            catch (JsonReaderException ex)
            {
                Log.Error(ex, $"=> Couldn't parse Dalamud output: {output}");
                return null;
            }

            Log.Verbose("=> Closing handles");

            return gameProcess;
        }
        catch (Exception ex)
        {
            throw new DalamudRunnerException("Error trying to start Dalamud.", ex);
        }
    }

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
        DuplicateOptions                     dwOptions
    );

    private static Process? GetInheritableCurrentProcessHandle()
    {
        if (!DuplicateHandle
            (
                Process.GetCurrentProcess().Handle,
                Process.GetCurrentProcess().Handle,
                Process.GetCurrentProcess().Handle,
                out var inheritableCurrentProcessHandle,
                0,
                true,
                DuplicateOptions.SameAccess
            ))
        {
            Log.Error("Failed to call DuplicateHandle: Win32 error code {0}", Marshal.GetLastWin32Error());
            return null;
        }

        return new ExistingProcess(inheritableCurrentProcessHandle);
    }

    private static IDictionary<string, string> SafeGetEnvVars()
    {
        var envVars = Environment.GetEnvironmentVariables();

        var envDict = new DictionaryWrapper
        (
            new Dictionary<string, string?>
            (
                envVars.Count,
                StringComparer.OrdinalIgnoreCase
            )
        );

        var       e  = envVars.GetEnumerator();
        using var e1 = e as IDisposable;

        Debug.Assert(e is not IDisposable, "Environment.GetEnvironmentVariables should not be IDisposable.");

        while (e.MoveNext())
        {
            var entry = e.Entry;
            envDict.Add((string)entry.Key, (string?)entry.Value);
        }

        return envDict.ToDictionary(pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class DictionaryWrapper
    (
        Dictionary<string, string?> contents
    ) : IDictionary<string, string?>, IDictionary
    {
        public ICollection<string>  Keys   => contents.Keys;
        public ICollection<string?> Values => contents.Values;

        public int Count => contents.Count;

        public           bool                        IsReadOnly     => ((IDictionary)contents).IsReadOnly;
        public           bool                        IsSynchronized => ((IDictionary)contents).IsSynchronized;
        public           bool                        IsFixedSize    => ((IDictionary)contents).IsFixedSize;
        public           object                      SyncRoot       => ((IDictionary)contents).SyncRoot;

        ICollection IDictionary.Keys => contents.Keys;
        ICollection IDictionary.Values => contents.Values;

        public void Add(string key, string? value) => this[key] = value;

        public void Add(KeyValuePair<string, string?> item) => Add(item.Key, item.Value);

        public void Add(object key, object? value) => Add((string)key, (string?)value);

        public void Clear() => contents.Clear();

        public bool Contains(KeyValuePair<string, string?> item) =>
            contents.ContainsKey(item.Key) && contents[item.Key] == item.Value;

        public bool Contains(object key) => ContainsKey((string)key);

        public bool ContainsKey(string key) => contents.ContainsKey(key);

        public bool ContainsValue(string? value) => contents.ContainsValue(value);

        public void CopyTo(KeyValuePair<string, string?>[] array, int arrayIndex) =>
            ((IDictionary<string, string?>)contents).CopyTo(array, arrayIndex);

        public void CopyTo(Array array, int index) => ((IDictionary)contents).CopyTo(array, index);

        public bool Remove(string key) => contents.Remove(key);

        public void Remove(object key) => Remove((string)key);

        public bool Remove(KeyValuePair<string, string?> item)
        {
            if (!Contains(item))
                return false;

            return Remove(item.Key);
        }

        public bool TryGetValue(string key, out string? value) => contents.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => contents.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => contents.GetEnumerator();

        IDictionaryEnumerator IDictionary.GetEnumerator() => ((IDictionary)contents).GetEnumerator();

        public string? this[string key]
        {
            get => contents[key];
            set => contents[key] = value;
        }

        public object? this[object key]
        {
            get => this[(string)key];
            set => this[(string)key] = (string?)value;
        }
    }

    private static string GetEnvironmentVariablesBlock(IDictionary<string, string> sd)
    {
        var keys = new string[sd.Count];
        sd.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.OrdinalIgnoreCase);

        var result = new StringBuilder(8 * keys.Length);

        foreach (var key in keys)
            result.Append(key).Append('=').Append(sd[key]).Append('\0');

        return result.ToString();
    }

    [Flags]
    private enum DuplicateOptions : uint
    {
        CloseSource = 0x00000001,
        SameAccess  = 0x00000002
    }
}
