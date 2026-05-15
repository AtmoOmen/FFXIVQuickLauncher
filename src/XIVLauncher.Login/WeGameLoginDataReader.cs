using System.ComponentModel;
using System.Diagnostics;
using XIVLauncher.ArgReader;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Login;

internal sealed class WeGameLoginDataReader
{
    public async Task<GameArgumentInterop.LoginData?> ReadAsync(CancellationTokenSource loginCancellationTokenSource, ILoginWorkflowInteraction interaction)
    {
        try
        {
            Process.Start
            (
                new ProcessStartInfo
                {
                    FileName        = "wegame://StartFor=2000340",
                    UseShellExecute = true
                }
            );
        }
        catch (Exception)
        {
            // ignored
        }

        var pidList = FFXIVProcess.GetGameProcessIDs().ToArray();

        try
        {
            await using var argReaderSession = await ArgReaderSession.StartAsync();

            while (true)
            {
                if (loginCancellationTokenSource.IsCancellationRequested)
                {
                    await argReaderSession.StopAsync(false);
                    return null;
                }

                await Task.Delay(1000);
                var newPidList = FFXIVProcess.GetGameProcessIDs().Except(pidList).ToArray();
                interaction.ShowLoginMessage("请使用 WeGame 登录需要读取的游戏账号并启动游戏");
#if DEBUG
                newPidList = FFXIVProcess.GetGameProcessIDs().ToArray();
#endif
                if (newPidList.Length == 0)
                    continue;

                var pid  = newPidList.First();
                var data = await argReaderSession.ReadLoginDataAsync(pid);
#if DEBUG
                interaction.ShowLoginMessage("读取成功");
#endif
                await argReaderSession.StopAsync(true);
                return data;
            }
        }
        catch (Win32Exception ex)
        {
            throw new Win32Exception($"错误: {ex.Message}\n请尝试手动运行 {Path.Combine(AppContext.BaseDirectory, "XIVLauncher.ArgReader.exe")} 后重启 XIVLauncherCN");
        }
    }
}
