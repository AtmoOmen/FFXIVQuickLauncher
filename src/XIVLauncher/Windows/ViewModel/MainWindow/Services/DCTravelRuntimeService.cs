using Serilog;
using XIVLauncher.Login;
using XIVLauncher.Common.Util;
using XIVLauncher.DCTravel;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Services;

public sealed class DCTravelRuntimeService : ILoginSessionRefreshSink, IDisposable
{
    private const int MAINTENANCE_RECOVERY_INTERVAL_MINUTES = 5;

    private readonly Action<string> setSdoAreaAction;

    private CancellationTokenSource? recoveryCts;
    private Task?                    recoveryTask;

    public DCTravelClient  Client    { get; }
    public DCTravelListener? Listener { get; private set; }

    /// <summary>
    ///     超域旅行维护状态变更事件, 供 ViewModel 订阅刷新 UI。
    /// </summary>
    public event Action<DCTravelMaintenanceState>? MaintenanceStateChanged;

    /// <summary>
    ///     当前超域旅行监听端口。0 表示未启动监听。
    /// </summary>
    public int DcTravelPort { get; private set; }

    public DCTravelRuntimeService(Action<string> setSdoAreaAction)
    {
        ArgumentNullException.ThrowIfNull(setSdoAreaAction);

        this.setSdoAreaAction = setSdoAreaAction;
        Client = new DCTravelClient(string.Empty)
        {
            SetSdoAreaFunc = name => this.setSdoAreaAction(name)
        };

        Client.MaintenanceDetected += () =>
        {
            Log.Warning("[DCTravelListener] 运行时检测到超域旅行服务维护, 启动恢复定时器");
            StartMaintenanceRecovery();
            MaintenanceStateChanged?.Invoke(DCTravelMaintenanceState.UnderMaintenance);
        };
    }

    public void Bind(LoginSessionRefreshContext context) =>
        Client.BindLoginSessionRefresh(context);

    public async Task<int> StartAsync(bool enableDalamud, bool skipDcTravel)
    {
        Stop();

        if (!enableDalamud || skipDcTravel)
            return 0;

        DcTravelPort = APIHelper.GetAvailablePort();

        // 无论初始化是否成功, 始终启动监听器 —— 游戏内插件可通过 RPC 错误区分维护状态
        Listener = new DCTravelListener(Client, DcTravelPort, false);
        _ = Listener.StartAsync();
        Log.Information("[DCTravelListener] 打开监听端口: {DcTravelPort}", DcTravelPort);

        try
        {
            await Client.GetValidCookie().ConfigureAwait(false);
            _ = Client.KeepCookieAlive();

            if (Client.MaintenanceState == DCTravelMaintenanceState.UnderMaintenance)
            {
                Log.Warning("[DCTravelListener] 超域旅行服务维护中, 启动后台恢复定时器");
                StartMaintenanceRecovery();
            }
        }
        catch (Exception ex)
        {
            // 初始化失败但监听器已启动, 插件会收到可读的错误
            Log.Warning(ex, "[DCTravelListener] 超域旅行初始化失败, 监听器仍在运行");

            if (Client.MaintenanceState == DCTravelMaintenanceState.UnderMaintenance)
                StartMaintenanceRecovery();
        }

        MaintenanceStateChanged?.Invoke(Client.MaintenanceState);
        return DcTravelPort;
    }

    public void ConfigureQuickLoginRefresh(Func<Task<string>> refreshGameSessionIdByQuickLoginFunc)
    {
        ArgumentNullException.ThrowIfNull(refreshGameSessionIdByQuickLoginFunc);
        Client.RefreshGameSessionIDByQuickLoginFunc = refreshGameSessionIdByQuickLoginFunc;
    }

    public void Stop()
    {
        StopMaintenanceRecovery();

        try
        {
            Listener?.Stop();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法关闭 DCTravelListener");
        }
        finally
        {
            Listener = null;
            DcTravelPort = 0;
        }
    }

    public void Dispose()
    {
        StopMaintenanceRecovery();
        recoveryCts?.Dispose();
    }

    #region 维护自动恢复

    private void StartMaintenanceRecovery()
    {
        if (recoveryTask is { IsCompleted: false })
            return;

        StopMaintenanceRecovery();

        recoveryCts = new CancellationTokenSource();
        recoveryTask = RunMaintenanceRecoveryLoopAsync(recoveryCts.Token);
    }

    private void StopMaintenanceRecovery()
    {
        recoveryCts?.Cancel();
        recoveryCts?.Dispose();
        recoveryCts  = null;
        recoveryTask = null;
    }

    private async Task RunMaintenanceRecoveryLoopAsync(CancellationToken ct)
    {
        Log.Information("[DCTravelListener] 维护恢复定时器已启动, 间隔 {Interval} 分钟", MAINTENANCE_RECOVERY_INTERVAL_MINUTES);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(MAINTENANCE_RECOVERY_INTERVAL_MINUTES), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var state = await Client.TryRecoverFromMaintenanceAsync().ConfigureAwait(false);

                if (state == DCTravelMaintenanceState.Normal)
                {
                    Log.Information("[DCTravelListener] 超域旅行服务已恢复, 重新启动保活");
                    // 旧 Listener 无需重启 —— 它引用同一 Client, 恢复后 RPC 控制器直接可用
                    _ = Client.KeepCookieAlive();

                    MaintenanceStateChanged?.Invoke(state);
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DCTravelListener] 维护恢复检查失败");
            }
        }

        Log.Information("[DCTravelListener] 维护恢复定时器已停止");
    }

    #endregion
}
