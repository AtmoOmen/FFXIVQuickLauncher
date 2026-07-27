using System.Diagnostics;
using Serilog;

namespace XIVLauncher.Dalamud;

public class DalamudSession
(
    IDalamudRunner              injector,
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
    public DalamudInstallState EnsureReady(DirectoryInfo gamePathDir)
    {
        Log.Information("[HOOKS] DalamudSession::EnsureReady(gp:{0})", gamePathDir.FullName);

        if (updater.State != DalamudUpdater.DownloadState.Done)
            updater.ShowLoading();

        updater.WaitForCompletion();

        if (updater.State == DalamudUpdater.DownloadState.NoIntegrity)
        {
            updater.HideLoading();
            throw new DalamudRunnerException("Dalamud 完整性检测或更新反复失败, 请检查你的本地网络环境", updater.EnsurementException);
        }

        if (updater.State != DalamudUpdater.DownloadState.Done)
            throw new DalamudRunnerException("Dalamud 更新器未能进入就绪状态");

        if (updater.Runner == null || !updater.Runner.Exists)
            throw new DalamudRunnerException("Dalamud 本地注入文件不存在, 请重新启动 XIVLauncher 以开始完整性检测与下载流程");

        return DalamudInstallState.Ok;
    }

    public void InjectGame(int gamePid, bool safeMode = false)
    {
        Log.Information("[HOOKS] DalamudSession::InjectGame(gp:{0})", gamePath.FullName);

        var startInfo = CreateStartInfo();

        var environment = new Dictionary<string, string>
        {
            ["DALAMUD_RUNTIME"]          = updater.Runtime.FullName,
            ["DOTNET_ROOT"]              = updater.Runtime.FullName,
            ["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        };

        DalamudInjector.Inject(updater.Runner!, gamePid, environment, startInfo, safeMode, noThirdPlugin, true);
    }

    public Process LaunchGame(FileInfo gameExe, string gameArgs, IDictionary<string, string> environment)
    {
        Log.Information("[Dalamud Session] 开始运行, 游戏路径: {0}", gamePath.FullName);

        var startInfo = CreateStartInfo();

        if (loadMethod != DalamudLoadMethod.ACLonly)
            Log.Information("[HOOKS] DelayInitializeMs: {0}", startInfo.DelayInitializeMs);

        switch (loadMethod)
        {
            case DalamudLoadMethod.EntryPoint:
                Log.Verbose("[HOOKS] Now running OEP rewrite");
                break;

            case DalamudLoadMethod.ACLonly:
                Log.Verbose("[HOOKS] Now running ACL-only fix without injection");
                break;
        }

        var process = injector.Run(updater.Runner!, fakeLogin, noPlugin, noThirdPlugin, gameExe, gameArgs, environment, loadMethod, startInfo);

        updater.HideLoading();

        if (loadMethod != DalamudLoadMethod.ACLonly)
            Log.Information("[HOOKS] Started dalamud!");

        return process ?? throw new DalamudRunnerException("无法启动游戏进程");
    }

    private DalamudStartInfo CreateStartInfo()
    {
        var ingamePluginPath = Path.Combine(configDirectory.FullName, "installedPlugins");
        Directory.CreateDirectory(ingamePluginPath);

        if (updater.AssetDirectory == null || updater.Runner == null)
            throw new DalamudRunnerException("Dalamud 资源尚未准备完成");

        return new DalamudStartInfo
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
    }

    public enum DalamudInstallState
    {
        Ok,
        OutOfDate
    }
}
