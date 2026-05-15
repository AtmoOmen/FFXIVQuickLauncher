using Serilog;
using XIVLauncher.Login;
using XIVLauncher.Common.Util;
using XIVLauncher.DCTravel;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Services;

public sealed class DcTravelRuntimeService : ILoginSessionRefreshSink
{
    private readonly Action<string> setSdoAreaAction;

    public DCTravelClient Client { get; }
    public DCTravelListener? Listener { get; private set; }

    public DcTravelRuntimeService(Action<string> setSdoAreaAction)
    {
        ArgumentNullException.ThrowIfNull(setSdoAreaAction);

        this.setSdoAreaAction = setSdoAreaAction;
        Client = new DCTravelClient(string.Empty)
        {
            SetSdoAreaFunc = name => this.setSdoAreaAction(name)
        };
    }

    public void Bind(LoginSessionRefreshContext context) =>
        Client.BindLoginSessionRefresh(context);

    public async Task<int> StartAsync(bool enableDalamud, bool skipDcTravel)
    {
        Stop();

        if (!enableDalamud || skipDcTravel)
            return 0;

        try
        {
            Log.Information("[DCTravelListener] 正在开启监听用端口");
            await Client.GetValidCookie().ConfigureAwait(false);
            _ = Client.KeepCookieAlive();

            var dcTravelPort = APIHelper.GetAvailablePort();
            Listener = new DCTravelListener(Client, dcTravelPort, false);
            Log.Information("[DCTravelListener] 打开监听端口: {DcTravelPort}", dcTravelPort);
            _ = Listener.StartAsync();
            return dcTravelPort;
        }
        catch (Exception ex)
        {
            Listener = null;
            Log.Warning(ex, "[DCTravelListener] 启动失败, 已跳过游戏内超域传送支持");
            return 0;
        }
    }

    public void ConfigureQuickLoginRefresh(Func<Task<string>> refreshGameSessionIdByQuickLoginFunc)
    {
        ArgumentNullException.ThrowIfNull(refreshGameSessionIdByQuickLoginFunc);
        Client.RefreshGameSessionIDByQuickLoginFunc = refreshGameSessionIdByQuickLoginFunc;
    }

    public void Stop()
    {
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
        }
    }
}
