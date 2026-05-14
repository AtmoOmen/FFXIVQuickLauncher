using System.Diagnostics;
using System.Text;
using Serilog;

namespace XIVLauncher.Dalamud;

public class DalamudLauncher
(
    IDalamudRunner              runner,
    DalamudUpdater              updater,
    DalamudLoadMethod           loadMethod,
    DirectoryInfo               gamePath,
    DirectoryInfo               configDirectory,
    DirectoryInfo               logPath,
    int                         injectionDelay,
    bool                        fakeLogin,
    bool                        noPlugin,
    bool                        noThirdPlugin,
    string                      troubleshootingData,
    IDalamudGameVersionProvider gameVersionProvider
)
{
    public DalamudInstallState HoldForUpdate(DirectoryInfo gamePathDir)
    {
        Log.Information("[HOOKS] DalamudLauncher::HoldForUpdate(gp:{0})", gamePathDir.FullName);

        if (updater.State != DalamudUpdater.DownloadState.Done)
            updater.ShowLoading();

        while (updater.State != DalamudUpdater.DownloadState.Done)
        {
            if (updater.State == DalamudUpdater.DownloadState.NoIntegrity)
            {
                updater.HideLoading();
                throw new DalamudRunnerException("Dalamud 完整性检测或更新反复失败, 请检查你的本地网络环境", updater.EnsurementException?.InnerException);
            }

            Thread.Yield();
        }

        if (updater.Runner == null || !updater.Runner.Exists)
            throw new DalamudRunnerException("Dalamud 本地注入文件不存在, 请重新启动 XIVLauncher 以开始完整性检测与下载流程");

        return DalamudInstallState.Ok;
    }

    public void Inject(int gamePid, bool safeMode = false)
    {
        Log.Information("[HOOKS] DalamudLauncher::Run(gp:{0})", gamePath.FullName);

        var ingamePluginPath = Path.Combine(configDirectory.FullName, "installedPlugins");

        Directory.CreateDirectory(ingamePluginPath);

        if (updater.AssetDirectory == null || updater.Runner == null)
            throw new DalamudRunnerException("Dalamud 资源尚未准备完成");

        var startInfo = new DalamudStartInfo
        {
            PluginDirectory         = ingamePluginPath,
            ConfigurationPath       = Path.Combine(configDirectory.FullName, "dalamudConfig.json"),
            LoggingPath             = logPath.FullName,
            AssetDirectory          = updater.AssetDirectory.FullName,
            GameVersion             = gameVersionProvider.GetVersion(gamePath),
            WorkingDirectory        = updater.Runner.Directory?.FullName ?? updater.Runner.DirectoryName ?? Environment.CurrentDirectory,
            DelayInitializeMs       = injectionDelay,
            TroubleshootingPackData = troubleshootingData,
            LauncherDirectory       = Environment.CurrentDirectory
        };

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

        var psi = new ProcessStartInfo(updater.Runner.FullName)
        {
            Arguments              = string.Join(" ", launchArguments),
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment =
            {
                ["DALAMUD_RUNTIME"]          = updater.Runtime.FullName,
                ["DOTNET_ROOT"]              = updater.Runtime.FullName,
                ["DOTNET_MULTILEVEL_LOOKUP"] = "0"
            }
        };

        var dalamudProcess = Process.Start(psi);

        while (dalamudProcess != null && !dalamudProcess.StandardOutput.EndOfStream)
        {
            var line = dalamudProcess.StandardOutput.ReadLine();
            if (line != null)
                Log.Information(line);
        }
    }

    public Process Run(FileInfo gameExe, string gameArgs, IDictionary<string, string> environment)
    {
        Log.Information("[Dalamud Launcher] 开始运行, 游戏路径: {0}", gamePath.FullName);

        var ingamePluginPath = Path.Combine(configDirectory.FullName, "installedPlugins");

        Directory.CreateDirectory(ingamePluginPath);

        if (updater.AssetDirectory == null || updater.Runner == null)
            throw new DalamudRunnerException("Dalamud 资源尚未准备完成");

        var startInfo = new DalamudStartInfo
        {
            PluginDirectory         = ingamePluginPath,
            ConfigurationPath       = Path.Combine(configDirectory.FullName, "dalamudConfig.json"),
            LoggingPath             = logPath.FullName,
            AssetDirectory          = updater.AssetDirectory.FullName,
            GameVersion             = gameVersionProvider.GetVersion(gamePath),
            WorkingDirectory        = updater.Runner.Directory?.FullName ?? updater.Runner.DirectoryName ?? Environment.CurrentDirectory,
            DelayInitializeMs       = injectionDelay,
            TroubleshootingPackData = troubleshootingData,
            LauncherDirectory       = Environment.CurrentDirectory
        };

        if (loadMethod != DalamudLoadMethod.ACLonly)
            Log.Information("[HOOKS] DelayInitializeMs: {0}", startInfo.DelayInitializeMs);

        switch (loadMethod)
        {
            case DalamudLoadMethod.EntryPoint:
                Log.Verbose("[HOOKS] Now running OEP rewrite");
                break;

            case DalamudLoadMethod.DllInject:
                Log.Verbose("[HOOKS] Now running DLL inject");
                break;

            case DalamudLoadMethod.ACLonly:
                Log.Verbose("[HOOKS] Now running ACL-only fix without injection");
                break;
        }

        var process = runner.Run(updater.Runner, fakeLogin, noPlugin, noThirdPlugin, gameExe, gameArgs, environment, loadMethod, startInfo);

        updater.HideLoading();

        if (loadMethod != DalamudLoadMethod.ACLonly)
            Log.Information("[HOOKS] Started dalamud!");

        return process ?? throw new DalamudRunnerException("无法启动游戏进程");
    }

    public enum DalamudInstallState
    {
        Ok,
        OutOfDate
    }
}
