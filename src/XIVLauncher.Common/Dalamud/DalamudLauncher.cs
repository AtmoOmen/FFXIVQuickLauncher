using System.Diagnostics;
using System.Text;
using Serilog;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Dalamud;

public class DalamudLauncher
(
    IDalamudRunner    runner,
    DalamudUpdater    updater,
    DalamudLoadMethod loadMethod,
    DirectoryInfo     gamePath,
    DirectoryInfo     configDirectory,
    DirectoryInfo     logPath,
    ClientLanguage    clientLanguage,
    int               injectionDelay,
    bool              fakeLogin,
    bool              noPlugin,
    bool              noThirdPlugin,
    string            troubleshootingData
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

        if (!updater.Runner.Exists)
            throw new DalamudRunnerException("Dalamud 本地注入文件不存在, 请重新启动 XIVLauncher 以开始完整性检测与下载流程");

        return DalamudInstallState.Ok;
    }

    public void Inject(int gamePid, bool safeMode = false)
    {
        Log.Information("[HOOKS] DalamudLauncher::Run(gp:{0}, cl:{1})", gamePath.FullName, clientLanguage);

        var ingamePluginPath = Path.Combine(configDirectory.FullName, "installedPlugins");

        Directory.CreateDirectory(ingamePluginPath);

        var startInfo = new DalamudStartInfo
        {
            Language                = clientLanguage,
            PluginDirectory         = ingamePluginPath,
            ConfigurationPath       = DalamudSettings.GetConfigPath(configDirectory),
            LoggingPath             = logPath.FullName,
            AssetDirectory          = updater.AssetDirectory.FullName,
            GameVersion             = Repository.Ffxiv.GetVer(gamePath),
            WorkingDirectory        = updater.Runner.Directory?.FullName,
            DelayInitializeMs       = injectionDelay,
            TroubleshootingPackData = troubleshootingData
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
            DalamudInjectorArgs.ClientLanguage((int)startInfo.Language),
            DalamudInjectorArgs.DelayInitialize(startInfo.DelayInitializeMs),
            DalamudInjectorArgs.TsPackB64(Convert.ToBase64String(Encoding.UTF8.GetBytes(startInfo.TroubleshootingPackData)))
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
        Log.Information("[HOOKS] DalamudLauncher::Run(gp:{0}, cl:{1})", gamePath.FullName, clientLanguage);

        var ingamePluginPath = Path.Combine(configDirectory.FullName, "installedPlugins");

        Directory.CreateDirectory(ingamePluginPath);

        var startInfo = new DalamudStartInfo
        {
            Language                = clientLanguage,
            PluginDirectory         = ingamePluginPath,
            ConfigurationPath       = DalamudSettings.GetConfigPath(configDirectory),
            LoggingPath             = logPath.FullName,
            AssetDirectory          = updater.AssetDirectory.FullName,
            GameVersion             = Repository.Ffxiv.GetVer(gamePath),
            WorkingDirectory        = updater.Runner.Directory?.FullName,
            DelayInitializeMs       = injectionDelay,
            TroubleshootingPackData = troubleshootingData
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

        return process;
    }

    public enum DalamudInstallState
    {
        Ok,
        OutOfDate
    }
}
