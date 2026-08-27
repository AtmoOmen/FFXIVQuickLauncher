using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using XIVLauncher.Account.DeviceProfiles;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.CompanionApp;
using XIVLauncher.Dalamud;
using XIVLauncher.GamePatchV3.Update;
using XIVLauncher.Login.Channels;
using XIVLauncher.Login.Client;
using XIVLauncher.Login.Models;
using XIVLauncher.Support;
using XIVLauncher.Windows.GameClientFiles;
using XIVLauncher.Windows.ViewModel.Main.Models;
using XIVLauncher.Windows.ViewModel.Main.Services;

namespace XIVLauncher.Windows.ViewModel.Main.Flows;

internal sealed class GameLaunchFlow
(
    MainWindowViewModel  vm,
    CompanionAppService  companionAppService,
    GameClientFileFlow   gameClientFileFlow,
    DalamudLaunchService dalamudLaunchService
)
{
    public async Task<FFXIVProcess?> StartGameAndCompanionApp
    (
        GameLaunchContext gameLaunchContext,
        bool              forceNoDalamud,
        bool              noThird,
        bool              noPlugins
    )
    {
        var loginResult = gameLaunchContext.LoginResult;
        var gamePath    = App.Settings.GetGamePath(gameLaunchContext.AccountType);

        if (gamePath?.Exists != true)
        {
            CustomMessageBox.Show("当前账号渠道的游戏目录无效, 请在设置中重新选择", "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: vm.Window);
            return null;
        }

        SyncGameLaunchContextAreaFromAccount(gameLaunchContext);

        if (!await EnsureFreshSessionIdAsync(gameLaunchContext).ConfigureAwait(false))
            return null;

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        var dalamudSession = dalamudLaunchService.CreateSession
        (
            gamePath,
            App.Settings.DalamudLoadMethod,
            (int)App.Settings.DalamudInjectionDelayMS,
            noPlugins,
            noThird
        );

        var dalamudOk = false;

        if (App.Settings.DalamudEnabled && !forceNoDalamud && dalamudLaunchService.EnsureCompatibility())
        {
            var (dalamudUpdateResult, dalamudUpdateErrorMessage) = dalamudLaunchService.TryUpdate(dalamudSession, gamePath, true);

            if (dalamudUpdateResult == DalamudPrepareResult.Failed && dalamudUpdateErrorMessage != null)
            {
                var continuationChoice = CustomMessageBox.Builder
                                                         .NewFrom($"{dalamudUpdateErrorMessage}\n点击确定将继续以不启用 Dalamud 的方式启动游戏")
                                                         .WithImage(MessageBoxImage.Warning)
                                                         .WithButtons(MessageBoxButton.OKCancel)
                                                         .WithShowHelpLinks()
                                                         .WithParentWindow(vm.Window)
                                                         .Show();

                if (continuationChoice != MessageBoxResult.OK)
                    return null;
            }

            dalamudOk = dalamudUpdateResult == DalamudPrepareResult.OK;
        }

        var dotnetRuntimePath = App.Dalamud.Updater.Runtime;
        stopwatch.Stop();

        if (stopwatch.Elapsed > TimeSpan.FromMinutes(5))
        {
            CustomMessageBox.Show("会话已过期,请重新登录", "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: vm.Window);
            return null;
        }

        Process? StartGame
        (
            GameStartRequest request
        )
        {
            Log.Information($"Game Exe:{request.ExePath}");

            if (dalamudOk)
            {
                var compat = "RunAsInvoker ";
                compat += request.DpiAwareness switch
                {
                    DPIAwareness.Aware   => "HighDPIAware",
                    DPIAwareness.Unaware => "DPIUnaware",
                    _                    => throw new ArgumentOutOfRangeException()
                };
                request.Environment.Add("__COMPAT_LAYER", compat);

                var prevDalamudRuntime = Environment.GetEnvironmentVariable("DALAMUD_RUNTIME");
                if (string.IsNullOrWhiteSpace(prevDalamudRuntime))
                    request.Environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath.FullName);

                var prevDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
                if (string.IsNullOrWhiteSpace(prevDotnetRoot))
                    request.Environment.Add("DOTNET_ROOT", dotnetRuntimePath.FullName);

                var prevDotnetLookup = Environment.GetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP");
                if (string.IsNullOrWhiteSpace(prevDotnetLookup))
                    request.Environment.Add("DOTNET_MULTILEVEL_LOOKUP", "0");

                return dalamudSession.LaunchGame(new FileInfo(request.ExePath), request.Arguments, request.Environment);
            }

            return NativeAclFix.LaunchGame
            (
                request.WorkingDirectory,
                request.ExePath,
                request.Arguments,
                request.Environment,
                request.DpiAwareness,
                process =>
                {
                    var argFix = new GameArgumentInterop.Fixer(process);
                    argFix.Fix();
                }
            );
        }

        // We won't do any sanity checks here anymore, since that should be handled in StartLogin
        var launched = vm.Launcher.LaunchGame
        (
            StartGame,
            loginResult.OAuthLogin!.SessionID,
            loginResult.OAuthLogin!.SndaID,
            gameLaunchContext.DcTravelPort,
            gameLaunchContext.Area.AreaID,
            gameLaunchContext.Area.AreaLobby,
            gameLaunchContext.Area.AreaGM,
            gameLaunchContext.Area.AreaConfigUpload,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gameLaunchContext.Areas))),
            App.Settings.AdditionalLaunchArgs,
            gamePath,
            App.Settings.EncryptArgumentsV2,
            App.Settings.DPIAwareness
        );

        Troubleshooting.LogTroubleshooting(gamePath);

        if (launched == null)
        {
            Log.Information("游戏进程为空");
            vm.IsLoggingIn = false;
            return null;
        }

        CompanionAppManager? companionAppManager = null;

        try
        {
            companionAppManager = companionAppService.StartCompanionApps(launched.ProcessID);
        }
        catch (Exception ex)
        {
            CustomMessageBox.Builder
                            .NewFrom(ex, "CompanionApps")
                            .WithAppendText("\n\n")
                            .WithAppendText("这可能由杀毒软件引起, 请检查日志并添加必要的排除项")
                            .WithParentWindow(vm.Window)
                            .Show();

            vm.IsLoggingIn = false;
        }

        Log.Debug("等待游戏进程退出");

        try
        {
            if (dalamudOk)
            {
                await vm.Launcher.RestartMonitor
                        .MonitorAsync
                        (
                            launched,
                            new RestartMonitor.RestartOptions
                            (
                                forceNoDalamud,
                                noThird,
                                noPlugins
                            ),
                            options => StartGameAndCompanionApp
                            (
                                gameLaunchContext,
                                options.ForceNoDalamud,
                                options.NoThirdPlugins,
                                options.NoPlugins
                            ),
                            vm.LoginFlow.LoginCancellationToken
                        )
                        .ConfigureAwait(false);
            }
            else
                await Task.Run(() => launched.UnderlyingProcess.WaitForExit()).ConfigureAwait(false);
        }
        finally
        {
            companionAppService.StopCompanionApps(launched.ProcessID, companionAppManager);
        }

        Log.Verbose("游戏进程已退出");

        return launched;
    }

    /// <summary>
    ///     启动游戏前实时换取一张全新的 session ticket。
    ///     ticket 单次有效且与登录时所连大区绑定，游戏外跨大区或进程重启后旧 ticket 会失效，
    ///     因此每次启动都必须用持久 TGT 现取，而非复用上次启动缓存的值。
    /// </summary>
    private async Task<bool> EnsureFreshSessionIdAsync
    (
        GameLaunchContext gameLaunchContext
    )
    {
        var oauthLogin = gameLaunchContext.LoginResult.OAuthLogin;
        if (oauthLogin == null)
            return true;

        if (!string.IsNullOrEmpty(oauthLogin.TGT) && !string.IsNullOrEmpty(oauthLogin.Guid))
        {
            try
            {
                var deviceProfile = oauthLogin.DeviceProfile ?? FakeMachineInfo.CreateSnapshot();
                var loginCtx      = new LoginChannelContext(deviceProfile);
                oauthLogin.SessionID = await loginCtx.GetSessionIdAsync(oauthLogin.TGT, oauthLogin.Guid).ConfigureAwait(false);
                Log.Information("[MainWindow] 已通过 TGT 实时获取 session ticket: {SessionID}", oauthLogin.SessionID[..Math.Min(8, oauthLogin.SessionID.Length)]);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] 通过 TGT 获取 session ticket 失败，登录可能已过期");
                CustomMessageBox.Show
                (
                    "登录会话已过期，请重新登录",
                    "XIVLauncherCN (Soil)",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    parentWindow: vm.Window
                );
                return false;
            }
        }

        if (string.IsNullOrEmpty(oauthLogin.SessionID))
        {
            Log.Error("[MainWindow] SessionID 为空且无法通过 TGT 获取，取消登录");
            CustomMessageBox.Show
            (
                "登录异常: SessionID 为空",
                "XIVLauncherCN (Soil)",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                showOfficialLauncher: true,
                parentWindow: vm.Window
            );
            return false;
        }

        return true;
    }

    private static void SyncGameLaunchContextAreaFromAccount
    (
        GameLaunchContext gameLaunchContext
    )
    {
        var account = App.AccountManager.CurrentAccount;
        if (account?.AreaName == null)
            return;

        if (string.Equals
            (
                account.AreaName,
                gameLaunchContext.Area.AreaName,
                StringComparison.Ordinal
            ))
            return;

        var matched = gameLaunchContext.Areas.FirstOrDefault
        (a =>
             string.Equals
             (
                 a.AreaName,
                 account.AreaName,
                 StringComparison.Ordinal
             )
        );

        if (matched != null)
        {
            gameLaunchContext.Area = matched;
            Log.Information
            (
                "[DCTravel] 启动前检测到大区与账号不同步，已更新为 \"{AreaName}\" (ID={AreaID})",
                account.AreaName,
                matched.AreaID
            );
        }
        else
        {
            Log.Warning
            (
                "[DCTravel] 启动前检测到大区与账号不同步 (账号: \"{AccountArea}\", 上下文: \"{ContextArea}\")，但无法在大区列表中找到匹配项",
                account.AreaName,
                gameLaunchContext.Area.AreaName
            );
        }
    }

    public void FakeStartGame()
    {
        if (vm.Window.Dispatcher != Dispatcher.CurrentDispatcher)
        {
            vm.Window.Dispatcher.Invoke(FakeStartGame);
            return;
        }

        Task.Run
        (() => StartGameAndCompanionApp
         (
             new GameLaunchContext
             (
                 new LoginResult
                 {
                     OAuthLogin = new OAuthLoginResult
                     {
                         MaxExpansion  = 5,
                         Playable      = true,
                         Region        = 0,
                         SessionID     = "0",
                         TermsAccepted = true,
                         SndaID        = "114514"
                     },
                     State    = LoginState.Ok,
                     UniqueID = "0"
                 },
                 vm.LoginPage.Area!,
                 vm.LoginPage.LoginAreas,
                 XIVAccountType.Sdo
             ),
             false,
             false,
             false
         )
        ).ConfigureAwait(false);
    }

    /// <summary>
    ///     从 Dashboard 启动游戏: 补丁前置检查 + while(true) 启动重启循环。
    /// </summary>
    public async Task<bool> LaunchGameWithRetryLoop
    (
        GameLaunchContext gameLaunchContext,
        LoginAfterAction  action
    )
    {
        var loginResult = gameLaunchContext.LoginResult;
        var gamePath    = App.Settings.GetGamePath(gameLaunchContext.AccountType);

        if (gamePath?.Exists != true)
        {
            CustomMessageBox.Show("当前账号渠道的游戏目录无效, 请在设置中重新选择", "XIVLauncherCN (Soil)", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: vm.Window);
            return false;
        }

        if (CustomMessageBox.AssertOrShowError
            (
                loginResult.State == LoginState.Ok,
                "LaunchGameWithRetryLoop: loginResult.State should have been LoginState.Ok",
                parentWindow: vm.Window
            ))
            return false;

        // 在启动游戏前检查是否有待安装的补丁
        try
        {
            var checkResult = await GameUpdater.Check
                              (
                                  gamePath,
                                  false,
                                  CancellationToken.None
                              ).ConfigureAwait(false);

            if (checkResult.NeedsUpdate)
            {
                if (!ConfirmGamePatchInstall())
                    return false;

                if (!await InstallGamePatchAsync(false).ConfigureAwait(false))
                {
                    Log.Error("patchSuccess != true");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MainWindow] 启动前补丁检查失败");
            CustomMessageBox.Builder
                            .NewFrom(ex, "启动前补丁检查")
                            .WithAppendText("\n\n检查游戏更新时发生错误，请稍后重试")
                            .WithParentWindow(vm.Window)
                            .Show();
            return false;
        }

        vm.Hide();

        while (true)
        {
            List<Exception> exceptions = [];

            try
            {
                var oauthLogin = loginResult.OAuthLogin;

                if (oauthLogin == null || string.IsNullOrEmpty(oauthLogin.SndaID))
                {
                    Log.Error("[MainWindow] SNDAID 为空，取消登录");
                    CustomMessageBox.Show
                    (
                        "登录异常: SNDAID 为空",
                        "XIVLauncherCN (Soil)",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        showOfficialLauncher: true,
                        parentWindow: vm.Window
                    );
                    return false;
                }

                using var process = await StartGameAndCompanionApp
                                    (
                                        gameLaunchContext,
                                        action == LoginAfterAction.StartWithoutDalamud,
                                        action == LoginAfterAction.StartWithoutThird,
                                        action == LoginAfterAction.StartWithoutPlugins
                                    ).ConfigureAwait(false);

                if (process == null)
                    return false;

                // 正常退出 / 重启
                if (process.ExitCode is not (0 or 0x12345678) && App.Settings.TreatNonZeroExitCodeAsFailure)
                {
                    var message = new CustomMessageBox.Builder()
                                  .WithTextFormatted
                                  (
                                      "游戏异常退出, 是否尝试重新启动?\n\n退出码: 0x{0:X8}",
                                      (uint)process.ExitCode
                                  )
                                  .WithImage(MessageBoxImage.Exclamation)
                                  .WithShowHelpLinks()
                                  .WithShowDiscordLink()
                                  .WithShowNewGitHubIssue()
                                  .WithButtons(MessageBoxButton.YesNoCancel)
                                  .WithDefaultResult(MessageBoxResult.Yes)
                                  .WithCancelResult(MessageBoxResult.No)
                                  .WithYesButtonText("重启")
                                  .WithNoButtonText("关闭")
                                  .WithCancelButtonText("不再询问")
                                  .WithParentWindow(vm.Window)
                                  .Show();

                    switch (message)
                    {
                        case MessageBoxResult.Yes:
                            continue;

                        case MessageBoxResult.No:
                            break;

                        case MessageBoxResult.Cancel:
                            App.Settings.TreatNonZeroExitCodeAsFailure = false;
                            break;
                    }
                }

                return true;
            }
            catch (AggregateException ex)
            {
                Log.Error(ex, "[MainWindow] 尝试处理登录结果时出现一个或多个异常");

                var innerException = ex.Flatten().InnerException;
                if (innerException != null)
                    exceptions.Add(innerException);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MainWindow] 尝试处理登录结果时出现异常");

                exceptions.Add(ex);
            }

            var builder = new CustomMessageBox.Builder()
                          .WithImage(MessageBoxImage.Error)
                          .WithShowHelpLinks()
                          .WithShowDiscordLink()
                          .WithShowNewGitHubIssue()
                          .WithButtons(MessageBoxButton.YesNo)
                          .WithDefaultResult(MessageBoxResult.No)
                          .WithCancelResult(MessageBoxResult.No)
                          .WithYesButtonText("重试")
                          .WithNoButtonText("关闭")
                          .WithParentWindow(vm.Window);

            List<string>  summaries    = [];
            List<string>  actionables  = [];
            List<string?> descriptions = [];

            foreach (var exception in exceptions)
            {
                switch (exception)
                {
                    case DalamudRunnerException:
                    case GameExitedException:
                        var count = 0;

                        foreach (var processName in new[] { "ffxiv_dx11", "ffxiv" })
                        {
                            foreach (var process in Process.GetProcessesByName(processName))
                            {
                                count++;
                                process.Dispose();
                            }
                        }

                        if (count >= 2)
                        {
                            summaries.Add("默认情况下无法启动两个以上游戏进程");
                            actionables.Add($"请检查是否存在尚未正常关闭的游戏进程 (当前: {count})");
                            descriptions.Add(null);

                            builder.WithButtons(MessageBoxButton.YesNoCancel)
                                   .WithDefaultResult(MessageBoxResult.Yes)
                                   .WithCancelButtonText("终止后重试");
                        }
                        else
                        {
                            summaries.Add("XIVLauncher 无法正确启动游戏");
                            descriptions.Add(null);
                        }

                        builder.WithShowNewGitHubIssue(false);

                        break;

                    case BinaryNotPresentException:
                        summaries.Add("找不到游戏可执行文件");
                        actionables.Add("可能需要重新安装游戏");
                        descriptions.Add(null);
                        break;

                    case IOException:
                        summaries.Add("无法找到游戏文件");
                        summaries.Add("请检查游戏路径设置情况");
                        descriptions.Add(exception.ToString());
                        descriptions.Add(exception.ToString());
                        break;

                    case Win32Exception win32Exception:
                        summaries.Add($"未知错误 (0x{(uint)win32Exception.HResult:X8}: {win32Exception.Message})");
                        descriptions.Add(exception.ToString());
                        break;

                    default:
                        summaries.Add($"未知错误 ({exception.Message})");
                        descriptions.Add(exception.ToString());
                        break;
                }
            }

            if (exceptions.Count == 1)
            {
                var summary     = summaries.ElementAtOrDefault(0) ?? "发生了未知错误";
                var actionable  = actionables.ElementAtOrDefault(0);
                var description = descriptions.ElementAtOrDefault(0) ?? string.Empty;
                var text = string.IsNullOrWhiteSpace(actionable) ?
                               summary :
                               $"{summary}\n\n{actionable}";

                builder.WithText(text)
                       .WithDescription(description);
            }
            else
            {
                builder.WithText("发生了多个错误");

                for (var i = 0; i < summaries.Count; i++)
                {
                    var summary     = summaries[i];
                    var actionable  = actionables.ElementAtOrDefault(i);
                    var description = descriptions.ElementAtOrDefault(i);

                    builder.WithAppendText($"\n{i + 1}. {summary}");

                    if (!string.IsNullOrWhiteSpace(actionable))
                        builder.WithAppendText($"\n    => {actionable}");
                    if (string.IsNullOrWhiteSpace(description))
                        continue;
                    builder.WithAppendDescription($"########## 异常 {i + 1} ##########\n{description}\n\n");
                }
            }

            if (descriptions.Any(x => x != null))
                builder.WithAppendSettingsDescription("Login");

            switch (builder.Show())
            {
                case MessageBoxResult.Yes:
                    continue;

                case MessageBoxResult.No:
                    return false;

                case MessageBoxResult.Cancel:
                    for (var pass = 0; pass < 8; pass++)
                    {
                        var allKilled = true;

                        foreach (var processName in new[] { "ffxiv_dx11", "ffxiv" })
                        {
                            foreach (var process in Process.GetProcessesByName(processName))
                            {
                                allKilled = false;

                                try
                                {
                                    process.Kill();
                                }
                                catch (Exception ex2)
                                {
                                    Log.Warning(ex2, "结束进程失败 (PID={0}, name={1})", process.Id, process.ProcessName);
                                }
                                finally
                                {
                                    process.Dispose();
                                }
                            }
                        }

                        if (allKilled)
                            break;
                    }

                    Task.Delay(1000).Wait();
                    continue;
            }
        }
    }

    #region 补丁

    private bool ConfirmGamePatchInstall()
    {
        if (!App.Settings.AskBeforePatchInstall)
            return true;

        var selfPatchAsk = CustomMessageBox.Show
        (
            "检测到新的游戏补丁\n是否下载更新文件并安装?",
            "XIVLauncherCN (Soil)",
            MessageBoxButton.YesNo,
            parentWindow: vm.Window
        );

        return selfPatchAsk != MessageBoxResult.No;
    }

    public async Task<bool> InstallGamePatchAsync
    (
        bool confirmInstallation
    )
    {
        if (confirmInstallation && !ConfirmGamePatchInstall())
            return false;

        await vm.GameUpdateMonitor.BeginUpdateAsync().ConfigureAwait(false);
        var succeeded = false;

        try
        {
            var accountType = vm.CurrentGameLaunchContext?.AccountType ??
                              vm.AccountManager.CurrentAccount?.AccountType ?? vm.LoginPage.LoginTypeOption.LoginType.ToAccountType(XIVAccountType.Sdo);
            var result = await gameClientFileFlow.RunAsync(GameClientFileTaskKind.Update, accountType).ConfigureAwait(false);
            succeeded = result.Status == GameClientFileTaskResultStatus.Success;

            if (succeeded)
                vm.Window.Dispatcher.Invoke(vm.DashboardFlow.RefreshGameVersion);

            return succeeded;
        }
        finally
        {
            vm.GameUpdateMonitor.CompleteUpdate(succeeded);
        }
    }

    #endregion
}
