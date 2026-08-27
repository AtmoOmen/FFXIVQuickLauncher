using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;
using XIVLauncher.CompanionApp;

namespace XIVLauncher.Windows.ViewModel.Main.Services;

public sealed class CompanionAppService
{
    private readonly ConcurrentDictionary<int, CompanionAppManager> companionAppManagers = [];

    public CompanionAppManager? StartCompanionApps
    (
        int gamePid
    )
    {
        var companionAppManager = new CompanionAppManager();

        if (!companionAppManagers.TryAdd(gamePid, companionAppManager))
        {
            Log.Information("伴随程序已随游戏进程启动: {GamePid}", gamePid);
            return null;
        }

        try
        {
            App.Settings.CompanionAppList ??= [];

            var companionApps = App.Settings.CompanionAppList
                                   .Where(entry => entry is { IsEnabled: true, CompanionApp: not null })
                                   .Select(entry => entry.CompanionApp)
                                   .ToList();

            companionAppManager.Start(companionApps);
            return companionAppManager;
        }
        catch
        {
            StopCompanionApps(gamePid, companionAppManager);
            throw;
        }
    }

    public void StopCompanionApps
    (
        int                  gamePid,
        CompanionAppManager? companionAppManager
    )
    {
        if (companionAppManager == null)
            return;

        if (!companionAppManagers.TryGetValue(gamePid, out var currentCompanionAppManager) || !ReferenceEquals(currentCompanionAppManager, companionAppManager))
            return;

        if (!companionAppManagers.TryRemove(gamePid, out _))
            return;

        companionAppManager.Stop();
    }

    public void StartCompanionAppsUntilGameExit
    (
        int gamePid
    )
    {
        var companionAppManager = StartCompanionApps(gamePid);
        if (companionAppManager == null)
            return;

        _ = Task.Run
        (async () =>
            {
                try
                {
                    using var process = Process.GetProcessById(gamePid);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "等待游戏进程退出时发生错误: {GamePid}", gamePid);
                }
                finally
                {
                    StopCompanionApps(gamePid, companionAppManager);
                }
            }
        );
    }
}
