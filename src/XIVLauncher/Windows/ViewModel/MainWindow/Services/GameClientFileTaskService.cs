using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Runtime;
using XIVLauncher.Common.Util;
using XIVLauncher.GamePatchV3;
using XIVLauncher.GamePatchV3.Install;
using XIVLauncher.GamePatchV3.Integrity;
using XIVLauncher.GamePatchV3.Integrity.Models;
using XIVLauncher.GamePatchV3.Models;
using XIVLauncher.GamePatchV3.Repair;
using XIVLauncher.GamePatchV3.Update;
using XIVLauncher.GamePatchV3.Update.Models;
using XIVLauncher.Windows.GameClientFiles;

namespace XIVLauncher.Windows.ViewModel.MainWindow.Services;

public sealed class GameClientFileTaskService
(
    Window window
)
{
    public async Task<GameClientFileTaskResult> RunAsync(GameClientFileTaskKind kind)
    {
        var viewModel = new GameClientFileTaskWindowViewModel();
        var dialog    = CreateWindow(viewModel);

        try
        {
            ShowWindow(dialog);
            return await RunCoreAsync(viewModel, kind).ConfigureAwait(false);
        }
        finally
        {
            CloseWindow(dialog);
        }
    }

    private async Task<GameClientFileTaskResult> RunCoreAsync(GameClientFileTaskWindowViewModel viewModel, GameClientFileTaskKind kind) =>
        kind switch
        {
            GameClientFileTaskKind.Update         => await RunUpdateAsync(viewModel).ConfigureAwait(false),
            GameClientFileTaskKind.Repair         => await RunRepairAsync(viewModel).ConfigureAwait(false),
            GameClientFileTaskKind.IntegrityCheck => await RunIntegrityCheckAsync(viewModel).ConfigureAwait(false),
            GameClientFileTaskKind.FreshInstall   => await RunFreshInstallAsync(viewModel).ConfigureAwait(false),
            _                                     => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知客户端文件任务类型")
        };

    private async Task<GameClientFileTaskResult> RunUpdateAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "更新游戏文件";

        Log.Information("[GameClientFileTask] 开始更新流程");

        if (!TryGetValidGamePath(out var gamePath, out var gamePathError))
        {
            if (App.Settings.GamePath != null && !string.IsNullOrWhiteSpace(App.Settings.GamePath.FullName))
            {
                Log.Warning("[GameClientFileTask] 游戏路径不存在, 提示是否安装游戏: {GamePath}", App.Settings.GamePath.FullName);
                var action = await WaitForChoiceAsync
                             (
                                 viewModel,
                                 CreateChoiceSnapshot(TITLE, "所选游戏目录不存在或尚未安装游戏", "是否下载安装完整游戏?", "开始安装", "关闭")
                             ).ConfigureAwait(false);

                if (action == GameClientFileTaskWindowAction.Primary)
                    return await RunFreshInstallAsync(viewModel).ConfigureAwait(false);
            }

            Log.Warning("[GameClientFileTask] 游戏路径无效: {Message}", gamePathError);
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, gamePathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
        }

        if (Repository.Ffxiv.IsBaseVer(gamePath))
        {
            Log.Warning("[GameClientFileTask] 当前游戏路径未检测到安装");
            var action = await WaitForChoiceAsync
                         (
                             viewModel,
                             CreateChoiceSnapshot(TITLE, "所选路径中没有检测到游戏安装", "是否下载安装完整游戏?", "开始安装", "关闭")
                         ).ConfigureAwait(false);

            if (action == GameClientFileTaskWindowAction.Primary)
                return await RunFreshInstallAsync(viewModel).ConfigureAwait(false);

            return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Failed };
        }

        if (!TryResolvePatchPath(out var patchPathError))
        {
            Log.Warning("[GameClientFileTask] 更新文件路径无效: {Message}", patchPathError);
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, patchPathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
        }

        Log.Information("[GameClientFileTask] 更新检查路径, 游戏 {GamePath}, 补丁 {PatchPath}", gamePath.FullName, App.Settings.PatchPath.FullName);

        using var cancellationTokenSource = new CancellationTokenSource();
        var       ctsBox                  = new StrongBox<CancellationTokenSource?>(cancellationTokenSource);
        SetRunningHandler(viewModel, () => ctsBox.Value?.Cancel());
        ApplySnapshot(viewModel, CreateRunningSnapshot(TITLE, "正在检查游戏更新"));

        GameUpdateCheckResult checkResult;

        try
        {
            Log.Information("[GameClientFileTask] 正在检查游戏更新");
            checkResult = await GameUpdater.Check(gamePath, false, cancellationTokenSource.Token).ConfigureAwait(false);
            Log.Information("[GameClientFileTask] 更新检查完成, 需要更新 {NeedsUpdate}", checkResult.NeedsUpdate);
        }
        catch (UnsupportedGameVersionException ex)
        {
            Log.Warning(ex, "[GameClientFileTask] 当前游戏版本不受更新功能支持");
            var action = await WaitForChoiceAsync
                         (
                             viewModel,
                             CreateChoiceSnapshot(TITLE, "当前游戏版本无法直接增量更新", ex.Message, "开始修复", "关闭")
                         ).ConfigureAwait(false);
            return action == GameClientFileTaskWindowAction.Primary
                       ? await RunRepairAsync(viewModel).ConfigureAwait(false)
                       : new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Failed };
        }
        catch (OperationCanceledException)
        {
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消更新检查"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GameClientFileTask] 更新检查失败");
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "检查游戏更新失败", ex.Message), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
        }

        if (!checkResult.NeedsUpdate)
            return await WaitForCloseAsync(viewModel, CreateSuccessSnapshot(TITLE, "更新检查已完成", "当前没有待安装的更新内容"), GameClientFileTaskResultStatus.Success).ConfigureAwait(false);

        if (checkResult.UpdatePlan == null)
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "获取游戏更新计划失败"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        return await RunGamePatchUpdateAsync(viewModel, checkResult.UpdatePlan).ConfigureAwait(false);
    }

    private async Task<GameClientFileTaskResult> RunGamePatchUpdateAsync(GameClientFileTaskWindowViewModel viewModel, GameUpdatePlan updatePlan)
    {
        const string TITLE = "更新游戏文件";

        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
        {
            Log.Warning("[GameClientFileTask] 更新互斥锁已被占用");
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "另一实例正在执行游戏更新", "请关闭其他 XIVLauncher 实例后重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
        }

        if (!AppUtil.TryYellOnGameFilesBeingOpen(window, _ => "关闭以下进程以更新游戏"))
        {
            Log.Warning("[GameClientFileTask] 检测到游戏文件被占用, 更新已取消");
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消更新"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        var       ctsBox                  = new StrongBox<CancellationTokenSource?>(cancellationTokenSource);
        SetRunningHandler(viewModel, () => ctsBox.Value?.Cancel());

        var progress = new Progress<GamePatchProgress>(value => ApplySnapshot(viewModel, CreateGamePatchSnapshot(value)));

        try
        {
            var assemblyLocation    = AppContext.BaseDirectory;
            var shimExecutablePath  = Path.Combine(assemblyLocation, "VcdiffShim", "XIVLauncher.VcdiffShim.exe");
            var adminAccessRequired = GameRepairer.AdminAccessRequired(App.Settings.GamePath.FullName);
            var shimRuntimePath     = DotNetRuntimeManager.GetRuntimeDirectory("win-x64");

            ApplySnapshot(viewModel, CreateRunningSnapshot(TITLE, "正在准备运行时"));
            var runtimeVersion = await DotNetRuntimeManager.GetLatestVersionAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            await DotNetRuntimeManager.EnsureRuntimeAsync
                                      (
                                          shimRuntimePath,
                                          runtimeVersion,
                                          "win-x64",
                                          "补丁安装器 .NET 运行时",
                                          message => ApplySnapshot(viewModel,                CreateRunningSnapshot(TITLE, message)),
                                          (total, downloaded, _) => ApplySnapshot(viewModel, CreateRuntimeDownloadSnapshot(TITLE, total, downloaded)),
                                          cancellationTokenSource.Token
                                      )
                                      .ConfigureAwait(false);

            Log.Information
            (
                "[GameClientFileTask] 准备安装更新, 当前 {CurrentGameVersion}/{CurrentDataVersion}, 目标 {TargetGameVersion}/{TargetDataVersion}, 包数量 {PackageCount}, 提权 {AdminAccessRequired}",
                updatePlan.CurrentGameVersion,
                updatePlan.CurrentDataVersion,
                updatePlan.TargetGameVersion,
                updatePlan.TargetDataVersion,
                updatePlan.Packages.Count,
                adminAccessRequired
            );

            using var vcdiffClient   = new VcdiffClient(shimExecutablePath, shimRuntimePath.FullName, adminAccessRequired);
            using var patchInstaller = new GamePatchInstaller();

            await patchInstaller.InstallAsync
                                (
                                    updatePlan,
                                    App.Settings.GamePath,
                                    App.Settings.PatchPath,
                                    vcdiffClient,
                                    App.Settings.KeepPatches,
                                    TimeSpan.FromMilliseconds(100),
                                    progress,
                                    cancellationTokenSource.Token
                                )
                                .ConfigureAwait(false);

            Log.Information("[GameClientFileTask] 游戏更新安装完成");
            return await WaitForCloseAsync(viewModel, CreateSuccessSnapshot(TITLE, "游戏更新已完成", "所有更新内容已安装完成"), GameClientFileTaskResultStatus.Success).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消更新"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GameClientFileTask] 游戏更新失败");
            var action = await WaitForChoiceAsync
                         (
                             viewModel,
                             CreateChoiceSnapshot(TITLE, "游戏更新失败", $"{ex.Message}\n可尝试修复游戏文件以恢复", "开始修复", "关闭")
                         ).ConfigureAwait(false);
            return action == GameClientFileTaskWindowAction.Primary
                       ? await RunRepairAsync(viewModel).ConfigureAwait(false)
                       : new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Failed };
        }
    }

    private async Task<GameClientFileTaskResult> RunRepairAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "修复游戏文件";

        if (!TryGetValidGamePath(out var gamePath, out var gamePathError))
        {
            if (App.Settings.GamePath != null && !string.IsNullOrWhiteSpace(App.Settings.GamePath.FullName))
            {
                Log.Warning("[GameClientFileTask] 游戏路径不存在, 提示是否安装游戏: {GamePath}", App.Settings.GamePath.FullName);
                var action = await WaitForChoiceAsync
                             (
                                 viewModel,
                                 CreateChoiceSnapshot(TITLE, "所选游戏目录不存在或尚未安装游戏", "是否下载安装完整游戏?", "开始安装", "关闭")
                             ).ConfigureAwait(false);

                if (action == GameClientFileTaskWindowAction.Primary)
                    return await RunFreshInstallAsync(viewModel).ConfigureAwait(false);
            }

            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, gamePathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
        }

        if (Repository.Ffxiv.IsBaseVer(gamePath))
        {
            Log.Warning("[GameClientFileTask] 当前游戏路径未检测到安装, 提示是否安装游戏");
            var action = await WaitForChoiceAsync
                         (
                             viewModel,
                             CreateChoiceSnapshot(TITLE, "所选路径中没有检测到游戏安装", "是否下载安装完整游戏?", "开始安装", "关闭")
                         ).ConfigureAwait(false);

            if (action == GameClientFileTaskWindowAction.Primary)
                return await RunFreshInstallAsync(viewModel).ConfigureAwait(false);

            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "已取消"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);
        }

        if (!TryResolvePatchPath(out var patchPathError))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, patchPathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (GameHelpers.CheckIsGameOpen())
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "官方启动器或游戏正在运行", "请关闭相关进程后重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        return await RunRepairerAsync(viewModel).ConfigureAwait(false);
    }

    private async Task<GameClientFileTaskResult> RunFreshInstallAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "安装游戏文件";

        if (!TryGetGamePath(out var gamePath, out var gamePathError))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, gamePathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (!TryResolvePatchPath(out var patchPathError))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, patchPathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (!gamePath.Exists)
            gamePath.Create();

        gamePath.CreateSubdirectory("game");
        gamePath.CreateSubdirectory("boot");

        return await RunInstallerAsync(viewModel).ConfigureAwait(false);
    }

    private async Task<GameClientFileTaskResult> RunIntegrityCheckAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "检查游戏完整性";

        if (!TryGetValidGamePath(out var gamePath, out var gamePathError))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, gamePathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        using var cancellationTokenSource = new CancellationTokenSource();
        var       ctsBox                  = new StrongBox<CancellationTokenSource?>(cancellationTokenSource);
        SetRunningHandler(viewModel, () => ctsBox.Value?.Cancel());
        ApplySnapshot(viewModel, CreateRunningSnapshot(TITLE, "正在获取完整性参考数据", true));

        IntegrityCheckCompareOutcome outcome;

        try
        {
            var progress = new Progress<IntegrityCheckProgress>(value => ApplySnapshot(viewModel, CreateIntegrityCheckRunningSnapshot(TITLE, value)));
            outcome = await GameIntegrityChecker.CompareIntegrityAsync(progress, gamePath, false, cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消完整性检查"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GameClientFileTask] 完整性检查失败");
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "检查游戏完整性失败", ex.Message), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
        }

        switch (outcome.CompareResult)
        {
            case IntegrityCheckCompareResult.VersionUnsupported:
            {
                var action = await WaitForChoiceAsync
                             (
                                 viewModel,
                                 CreateChoiceSnapshot
                                 (
                                     TITLE,
                                     "当前游戏版本无法直接检查完整性",
                                     "当前游戏数据版本过旧或无法识别\n请先使用“修复游戏文件”更新到最新版本, 或重新下载完整游戏",
                                     "开始修复",
                                     "关闭"
                                 )
                             ).ConfigureAwait(false);

                if (action == GameClientFileTaskWindowAction.Primary)
                    return await RunRepairAsync(viewModel).ConfigureAwait(false);

                return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Failed };
            }

            case IntegrityCheckCompareResult.ReferenceNotFound:
                return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "当前游戏版本没有可用的完整性参考", "请先更新游戏后再重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

            case IntegrityCheckCompareResult.ReferenceFetchFailure:
                return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "下载完整性检查参考文件失败", "请检查网络连接后重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

            case IntegrityCheckCompareResult.Valid:
                return await WaitForCloseAsync(viewModel, CreateSuccessSnapshot(TITLE, "完整性检查已完成", "游戏安装完整"), GameClientFileTaskResultStatus.Success).ConfigureAwait(false);

            case IntegrityCheckCompareResult.Invalid:
            {
                var reportPath = Path.Combine(Paths.RoamingPath, "integrityreport.txt");
                await File.WriteAllTextAsync(reportPath, outcome.Report, cancellationTokenSource.Token);
                var action = await WaitForChoiceAsync
                             (
                                 viewModel,
                                 CreateChoiceSnapshot
                                 (
                                     TITLE,
                                     "检测到部分游戏文件可能已被修改或损坏",
                                     $"完整性报告已保存到\n{reportPath}",
                                     "开始修复",
                                     "关闭"
                                 )
                             ).ConfigureAwait(false);

                if (action == GameClientFileTaskWindowAction.Primary)
                    return await RunRepairAsync(viewModel).ConfigureAwait(false);

                return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.IntegrityInvalid };
            }

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private async Task<GameClientFileTaskResult> RunRepairerAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "修复游戏文件";

        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "另一实例正在执行游戏更新", "请关闭其他 XIVLauncher 实例后重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (!AppUtil.TryYellOnGameFilesBeingOpen(window, _ => "关闭以下进程以修复游戏"))
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消修复"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);

        while (true)
        {
            var repairer              = new GameRepairer(App.Settings.GamePath.FullName, TimeSpan.FromMilliseconds(100));
            var repairerBox           = new StrongBox<GameRepairer?>(repairer);
            var cancellationRequested = false;

            using var pollCancellationTokenSource = new CancellationTokenSource();
            SetRunningHandler
            (
                viewModel,
                () =>
                {
                    if (cancellationRequested)
                        return;

                    var currentRepairer = repairerBox.Value;
                    if (currentRepairer == null)
                        return;

                    cancellationRequested = true;
                    ApplySnapshot(viewModel, CreateDisabledCancelSnapshot(CreateRepairerSnapshot(currentRepairer)));
                    currentRepairer.Cancel();
                }
            );

            var pollingTask = PollRepairerAsync(viewModel, repairer, pollCancellationTokenSource.Token);
            var runTask     = repairer.RunAsync(pollCancellationTokenSource.Token);

            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            pollCancellationTokenSource.Cancel();
            await AwaitPollingTaskAsync(pollingTask).ConfigureAwait(false);

            switch (repairer.State)
            {
                case GameRepairer.RepairState.Done:
                {
                    var action = await WaitForChoiceAsync(viewModel, CreateRepairCompletedSnapshot(repairer)).ConfigureAwait(false);

                    if (action == GameClientFileTaskWindowAction.Primary)
                        return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Success, ShouldLaunchGame = true };

                    return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Success };
                }

                case GameRepairer.RepairState.Error:
                {
                    var action = await WaitForChoiceAsync(viewModel, CreateRepairFailureSnapshot(repairer)).ConfigureAwait(false);
                    if (action == GameClientFileTaskWindowAction.Primary)
                        continue;

                    return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Failed };
                }

                case GameRepairer.RepairState.Cancelled:
                    return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消修复"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);

                default:
                    return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "修复失败", "任务未能正常完成"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
            }
        }
    }

    private async Task<GameClientFileTaskResult> RunInstallerAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "安装游戏文件";

        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "另一实例正在执行游戏更新", "请关闭其他 XIVLauncher 实例后重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (!AppUtil.TryYellOnGameFilesBeingOpen(window, _ => "关闭以下进程以安装游戏"))
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消安装"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);

        while (true)
        {
            var installer             = new GameInstaller(App.Settings.GamePath.FullName, TimeSpan.FromMilliseconds(100));
            var installerBox          = new StrongBox<GameInstaller?>(installer);
            var cancellationRequested = false;

            using var pollCancellationTokenSource = new CancellationTokenSource();
            SetRunningHandler
            (
                viewModel,
                () =>
                {
                    if (cancellationRequested)
                        return;

                    var currentInstaller = installerBox.Value;
                    if (currentInstaller == null)
                        return;

                    cancellationRequested = true;
                    ApplySnapshot(viewModel, CreateDisabledCancelSnapshot(CreateInstallerSnapshot(currentInstaller)));
                    currentInstaller.Cancel();
                }
            );

            var pollingTask = PollInstallerAsync(viewModel, installer, pollCancellationTokenSource.Token);
            var runTask     = installer.RunAsync(pollCancellationTokenSource.Token);

            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            pollCancellationTokenSource.Cancel();
            await AwaitPollingTaskAsync(pollingTask).ConfigureAwait(false);

            switch (installer.State)
            {
                case GameInstaller.InstallState.Done:
                {
                    var action = await WaitForChoiceAsync(viewModel, CreateInstallCompletedSnapshot(installer)).ConfigureAwait(false);

                    if (action == GameClientFileTaskWindowAction.Primary)
                        return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Success, ShouldLaunchGame = true };

                    return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Success };
                }

                case GameInstaller.InstallState.Error:
                {
                    var action = await WaitForChoiceAsync(viewModel, CreateInstallFailureSnapshot(installer)).ConfigureAwait(false);
                    if (action == GameClientFileTaskWindowAction.Primary)
                        continue;

                    return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Failed };
                }

                case GameInstaller.InstallState.Cancelled:
                    return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消安装"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);

                default:
                    return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "安装失败", "任务未能正常完成"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
            }
        }
    }

    private async Task PollRepairerAsync(GameClientFileTaskWindowViewModel viewModel, GameRepairer repairer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ApplySnapshot(viewModel, CreateRepairerSnapshot(repairer));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollInstallerAsync(GameClientFileTaskWindowViewModel viewModel, GameInstaller installer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ApplySnapshot(viewModel, CreateInstallerSnapshot(installer));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static GameClientFileTaskSnapshot CreateRepairerSnapshot(GameRepairer repairer)
    {
        return repairer.State switch
        {
            GameRepairer.RepairState.DownloadMeta => new GameClientFileTaskSnapshot
            {
                Title                   = "修复游戏文件",
                PhaseText               = "正在获取修复清单",
                DetailText              = repairer.CurrentFile,
                Progress                = repairer.Total == 0 ? 0 : 100.0 * repairer.Progress / repairer.Total,
                IsProgressIndeterminate = false,
                StatusText              = $"{APIHelper.BytesToString(repairer.Progress)}/{APIHelper.BytesToString(repairer.Total)}",
                SpeedText               = $"{APIHelper.BytesToString(repairer.Speed)}/s",
                EtaText                 = FormatEstimatedTime(repairer.Total - repairer.Progress, repairer.Speed),
                PrimaryButtonText       = "取消",
                IsPrimaryButtonVisible  = true,
                IsPrimaryButtonEnabled  = true,
                IsRunning               = true
            },
            GameRepairer.RepairState.Repairing => new GameClientFileTaskSnapshot
            {
                Title = "修复游戏文件",
                PhaseText = repairer.CurrentMetaInstallState == GameFileDownloader.InstallTaskState.NotStarted ? "正在验证游戏文件" : "正在修复游戏文件",
                DetailText = repairer.CurrentFile,
                Progress = repairer.Total == 0 ? 0 : 100.0 * repairer.Progress / repairer.Total,
                IsProgressIndeterminate = false,
                StatusText = $"{Math.Min(repairer.TaskIndex + 1, repairer.TaskCount)}/{repairer.TaskCount} - {APIHelper.BytesToString(repairer.Progress)}/{APIHelper.BytesToString(repairer.Total)}",
                SpeedText = GetDownloaderSpeedText(repairer.CurrentMetaInstallState, repairer.Speed),
                EtaText = GetDownloaderEtaText(repairer.CurrentMetaInstallState, repairer.Total - repairer.Progress, repairer.Speed),
                Items = repairer.IsDownloading ? GetRepairerItems(repairer) : [],
                PrimaryButtonText = repairer.CurrentMetaInstallState == GameFileDownloader.InstallTaskState.NotStarted ? string.Empty : "取消",
                IsPrimaryButtonVisible = repairer.CurrentMetaInstallState != GameFileDownloader.InstallTaskState.NotStarted,
                IsPrimaryButtonEnabled = repairer.CurrentMetaInstallState != GameFileDownloader.InstallTaskState.NotStarted,
                IsRunning = true
            },
            _ => new GameClientFileTaskSnapshot
            {
                Title                   = "修复游戏文件",
                PhaseText               = "正在准备修复任务",
                Progress                = repairer.State == GameRepairer.RepairState.Done ? 100 : 0,
                IsProgressIndeterminate = repairer.State != GameRepairer.RepairState.Done,
                IsRunning               = true
            }
        };
    }

    private static GameClientFileTaskSnapshot CreateInstallerSnapshot(GameInstaller installer)
    {
        return installer.State switch
        {
            GameInstaller.InstallState.DownloadMeta => new GameClientFileTaskSnapshot
            {
                Title                   = "安装游戏文件",
                PhaseText               = "正在获取游戏文件清单",
                DetailText              = installer.CurrentFile,
                Progress                = installer.Total == 0 ? 0 : 100.0 * installer.Progress / installer.Total,
                IsProgressIndeterminate = false,
                StatusText              = $"{APIHelper.BytesToString(installer.Progress)}/{APIHelper.BytesToString(installer.Total)}",
                SpeedText               = $"{APIHelper.BytesToString(installer.Speed)}/s",
                EtaText                 = FormatEstimatedTime(installer.Total - installer.Progress, installer.Speed),
                PrimaryButtonText       = "取消",
                IsPrimaryButtonVisible  = true,
                IsPrimaryButtonEnabled  = true,
                IsRunning               = true
            },
            GameInstaller.InstallState.Installing => new GameClientFileTaskSnapshot
            {
                Title                   = "安装游戏文件",
                PhaseText               = installer.CurrentMetaInstallState == GameFileDownloader.InstallTaskState.NotStarted ? "正在验证游戏文件" : "正在下载游戏文件",
                DetailText              = installer.CurrentFile,
                Progress                = installer.Total == 0 ? 0 : 100.0 * installer.Progress / installer.Total,
                IsProgressIndeterminate = false,
                StatusText =
                    $"{Math.Min(installer.TaskIndex + 1, installer.TaskCount)}/{installer.TaskCount} - {APIHelper.BytesToString(installer.Progress)}/{APIHelper.BytesToString(installer.Total)}",
                SpeedText              = GetDownloaderSpeedText(installer.CurrentMetaInstallState, installer.Speed),
                EtaText                = GetDownloaderEtaText(installer.CurrentMetaInstallState, installer.Total - installer.Progress, installer.Speed),
                PrimaryButtonText      = installer.CurrentMetaInstallState == GameFileDownloader.InstallTaskState.NotStarted ? string.Empty : "取消",
                IsPrimaryButtonVisible = installer.CurrentMetaInstallState != GameFileDownloader.InstallTaskState.NotStarted,
                IsPrimaryButtonEnabled = installer.CurrentMetaInstallState != GameFileDownloader.InstallTaskState.NotStarted,
                IsRunning              = true
            },
            _ => new GameClientFileTaskSnapshot
            {
                Title                   = "安装游戏文件",
                PhaseText               = "正在准备安装任务",
                Progress                = installer.State == GameInstaller.InstallState.Done ? 100 : 0,
                IsProgressIndeterminate = installer.State != GameInstaller.InstallState.Done,
                IsRunning               = true
            }
        };
    }

    private static GameClientFileTaskSnapshot CreateGamePatchSnapshot(GamePatchProgress progress)
    {
        var statusText = progress.StatusText;

        if (string.IsNullOrWhiteSpace(statusText) && progress.Total > 0)
        {
            statusText = progress.IsByteProgress
                             ? $"{APIHelper.BytesToString(progress.Progress)}/{APIHelper.BytesToString(progress.Total)}"
                             : $"{progress.Progress}/{progress.Total}";
        }

        return new()
        {
            Title                   = "更新游戏文件",
            PhaseText               = string.IsNullOrWhiteSpace(progress.PhaseText) ? "正在准备更新任务" : progress.PhaseText,
            DetailText              = progress.CurrentFile,
            Progress                = progress.Total == 0 ? 0 : 100.0 * progress.Progress / progress.Total,
            IsProgressIndeterminate = progress.Total == 0,
            StatusText              = statusText,
            SpeedText               = progress.Speed > 0 ? $"{APIHelper.BytesToString(progress.Speed)}/s" : string.Empty,
            EtaText                 = progress.IsByteProgress ? FormatEstimatedTime(progress.Total - progress.Progress, progress.Speed) : string.Empty,
            PrimaryButtonText       = "取消",
            IsPrimaryButtonVisible  = true,
            IsPrimaryButtonEnabled  = true,
            IsRunning               = true
        };
    }

    private static IReadOnlyList<GameClientFileTaskItemSnapshot> GetRepairerItems(GameRepairer repairer) =>
        repairer.GetCurrentInstallProgressEntries()
                .OrderBy(entry => entry.Key)
                .Take(8)
                .Select
                (entry => new GameClientFileTaskItemSnapshot
                    {
                        Title           = entry.Value.FilePath,
                        Progress        = entry.Value.Total == 0 ? 0 : 100.0 * entry.Value.Progress / entry.Value.Total,
                        IsIndeterminate = false
                    }
                )
                .ToArray();

    private static string GetDownloaderSpeedText(GameFileDownloader.InstallTaskState state, long speed) =>
        state switch
        {
            GameFileDownloader.InstallTaskState.Connecting => "正在连接",
            _                                              => $"{APIHelper.BytesToString(speed)}/s"
        };

    private static string GetDownloaderEtaText(GameFileDownloader.InstallTaskState state, long remaining, long speed) =>
        state switch
        {
            GameFileDownloader.InstallTaskState.Connecting => string.Empty,
            _                                              => FormatEstimatedTime(remaining, speed)
        };

    private static GameClientFileTaskSnapshot CreateInstallCompletedSnapshot(GameInstaller installer) =>
        new()
        {
            Title                  = "安装游戏文件",
            PhaseText              = "安装已完成",
            DetailText             = $"游戏文件安装完成, 共下载 {installer.TaskCount} 个文件",
            PrimaryButtonText      = "启动游戏",
            IsPrimaryButtonVisible = true,
            IsPrimaryButtonEnabled = true,
            CloseButtonText        = "关闭",
            IsCloseButtonVisible   = true,
            IsCloseButtonEnabled   = true
        };

    private static GameClientFileTaskSnapshot CreateRepairCompletedSnapshot(GameRepairer repairer)
    {
        var detailText = repairer.NumBrokenFiles switch
        {
            0 => "未检测到任何损坏的游戏文件",
            _ => $"已成功修复 {repairer.NumBrokenFiles} 个游戏文件"
        };

        if (repairer.MovedFiles.Count != 0)
            detailText = $"{detailText}\n已将 {repairer.MovedFiles.Count} 个非原始游戏文件移动至\n{repairer.MovedFileToDir}";

        return new GameClientFileTaskSnapshot
        {
            Title                  = "修复游戏文件",
            PhaseText              = "修复已完成",
            DetailText             = detailText,
            PrimaryButtonText      = "启动游戏",
            IsPrimaryButtonVisible = true,
            IsPrimaryButtonEnabled = true,
            CloseButtonText        = "关闭",
            IsCloseButtonVisible   = true,
            IsCloseButtonEnabled   = true
        };
    }

    private static GameClientFileTaskSnapshot CreateRepairFailureSnapshot(GameRepairer repairer) =>
        new()
        {
            Title                  = "修复游戏文件",
            PhaseText              = "修复未完成",
            DetailText             = "修复失败: 可能需要重新安装游戏",
            PrimaryButtonText      = "重试",
            IsPrimaryButtonVisible = true,
            IsPrimaryButtonEnabled = true,
            CloseButtonText        = "关闭",
            IsCloseButtonVisible   = true,
            IsCloseButtonEnabled   = true
        };

    private static GameClientFileTaskSnapshot CreateInstallFailureSnapshot(GameInstaller installer) =>
        new()
        {
            Title                  = "安装游戏文件",
            PhaseText              = "安装未完成",
            DetailText             = "安装失败: 可能需要检查网络连接",
            PrimaryButtonText      = "重试",
            IsPrimaryButtonVisible = true,
            IsPrimaryButtonEnabled = true,
            CloseButtonText        = "关闭",
            IsCloseButtonVisible   = true,
            IsCloseButtonEnabled   = true
        };

    private static GameClientFileTaskSnapshot CreateIntegrityCheckRunningSnapshot(string title, IntegrityCheckProgress progress)
    {
        var phaseText = string.IsNullOrWhiteSpace(progress.PhaseText) ? "正在检查游戏文件完整性" : progress.PhaseText;
        var percent   = progress.TotalFileCount == 0 ? 0 : 100.0 * progress.ProcessedFileCount / progress.TotalFileCount;

        return new GameClientFileTaskSnapshot
        {
            Title                   = title,
            PhaseText               = phaseText,
            DetailText              = progress.CurrentFile,
            Progress                = percent,
            IsProgressIndeterminate = progress.TotalFileCount == 0,
            StatusText              = progress.TotalFileCount == 0 ? string.Empty : $"{progress.ProcessedFileCount}/{progress.TotalFileCount}",
            PrimaryButtonText       = "取消",
            IsPrimaryButtonVisible  = true,
            IsPrimaryButtonEnabled  = true,
            IsRunning               = true
        };
    }

    private static GameClientFileTaskSnapshot CreateDisabledCancelSnapshot(GameClientFileTaskSnapshot snapshot) =>
        new()
        {
            Title                    = snapshot.Title,
            PhaseText                = snapshot.PhaseText,
            DetailText               = snapshot.DetailText,
            Progress                 = snapshot.Progress,
            IsProgressIndeterminate  = snapshot.IsProgressIndeterminate,
            StatusText               = snapshot.StatusText,
            SpeedText                = snapshot.SpeedText,
            EtaText                  = snapshot.EtaText,
            Items                    = snapshot.Items,
            PrimaryButtonText        = snapshot.PrimaryButtonText,
            IsPrimaryButtonVisible   = snapshot.IsPrimaryButtonVisible,
            IsPrimaryButtonEnabled   = false,
            SecondaryButtonText      = snapshot.SecondaryButtonText,
            IsSecondaryButtonVisible = snapshot.IsSecondaryButtonVisible,
            IsSecondaryButtonEnabled = snapshot.IsSecondaryButtonEnabled,
            CloseButtonText          = snapshot.CloseButtonText,
            IsCloseButtonVisible     = snapshot.IsCloseButtonVisible,
            IsCloseButtonEnabled     = snapshot.IsCloseButtonEnabled,
            IsRunning                = snapshot.IsRunning
        };

    private static GameClientFileTaskSnapshot CreateRunningSnapshot(string title, string phaseText, bool showPrimaryButton = false) =>
        new()
        {
            Title                   = title,
            PhaseText               = phaseText,
            IsProgressIndeterminate = true,
            PrimaryButtonText       = showPrimaryButton ? "取消" : string.Empty,
            IsPrimaryButtonVisible  = showPrimaryButton,
            IsPrimaryButtonEnabled  = showPrimaryButton,
            IsRunning               = true
        };

    private static GameClientFileTaskSnapshot CreateRuntimeDownloadSnapshot(string title, long? total, long downloaded) =>
        new()
        {
            Title                   = title,
            PhaseText               = "正在下载补丁安装器运行时",
            Progress                = total > 0 ? 100.0 * downloaded / total.Value : 0,
            IsProgressIndeterminate = total <= 0,
            StatusText              = total > 0 ? $"{APIHelper.BytesToString(downloaded)}/{APIHelper.BytesToString(total.Value)}" : APIHelper.BytesToString(downloaded),
            PrimaryButtonText       = "取消",
            IsPrimaryButtonVisible  = true,
            IsPrimaryButtonEnabled  = true,
            IsRunning               = true
        };

    private static GameClientFileTaskSnapshot CreateSuccessSnapshot(string title, string phaseText, string detailText) =>
        new()
        {
            Title                = title,
            PhaseText            = phaseText,
            DetailText           = detailText,
            Progress             = 100,
            CloseButtonText      = "关闭",
            IsCloseButtonVisible = true,
            IsCloseButtonEnabled = true
        };

    private static GameClientFileTaskSnapshot CreateFailureSnapshot(string title, string phaseText, string detailText = "") =>
        new()
        {
            Title                = title,
            PhaseText            = phaseText,
            DetailText           = detailText,
            CloseButtonText      = "关闭",
            IsCloseButtonVisible = true,
            IsCloseButtonEnabled = true
        };

    private static GameClientFileTaskSnapshot CreateCancelledSnapshot(string title, string phaseText) =>
        new()
        {
            Title                = title,
            PhaseText            = phaseText,
            CloseButtonText      = "关闭",
            IsCloseButtonVisible = true,
            IsCloseButtonEnabled = true
        };

    private static GameClientFileTaskSnapshot CreateChoiceSnapshot
    (
        string title,
        string phaseText,
        string detailText,
        string primaryButtonText,
        string closeButtonText
    ) =>
        new()
        {
            Title                  = title,
            PhaseText              = phaseText,
            DetailText             = detailText,
            PrimaryButtonText      = primaryButtonText,
            IsPrimaryButtonVisible = true,
            IsPrimaryButtonEnabled = true,
            CloseButtonText        = closeButtonText,
            IsCloseButtonVisible   = true,
            IsCloseButtonEnabled   = true
        };

    private async Task<GameClientFileTaskResult> WaitForCloseAsync
    (
        GameClientFileTaskWindowViewModel viewModel,
        GameClientFileTaskSnapshot        snapshot,
        GameClientFileTaskResultStatus    status
    )
    {
        await WaitForChoiceAsync(viewModel, snapshot).ConfigureAwait(false);
        return new GameClientFileTaskResult { Status = status };
    }

    private async Task<GameClientFileTaskWindowAction> WaitForChoiceAsync(GameClientFileTaskWindowViewModel viewModel, GameClientFileTaskSnapshot snapshot)
    {
        var actionSource = new TaskCompletionSource<GameClientFileTaskWindowAction>();
        SetActionHandler
        (
            viewModel,
            action => actionSource.TrySetResult(action)
        );
        ApplySnapshot(viewModel, snapshot);
        return await actionSource.Task.ConfigureAwait(false);
    }

    private void SetRunningHandler(GameClientFileTaskWindowViewModel viewModel, Action cancelAction) =>
        SetActionHandler
        (
            viewModel,
            action =>
            {
                if (action == GameClientFileTaskWindowAction.Primary)
                    cancelAction();
            }
        );

    private void ApplySnapshot(GameClientFileTaskWindowViewModel viewModel, GameClientFileTaskSnapshot snapshot) =>
        window.Dispatcher.Invoke(() => viewModel.ApplySnapshot(snapshot));

    private void SetActionHandler(GameClientFileTaskWindowViewModel viewModel, Action<GameClientFileTaskWindowAction>? handler) =>
        window.Dispatcher.Invoke(() => viewModel.ActionRequested = handler);

    private static async Task AwaitPollingTaskAsync(Task pollingTask)
    {
        try
        {
            await pollingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool TryResolvePatchPath(out string errorMessage)
    {
        try
        {
            App.Settings.PatchPath = Paths.ResolvePatchPath(App.Settings.PatchPath, Paths.RoamingPath);
            errorMessage           = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"解析更新文件路径失败\n{ex.Message}";
            return false;
        }
    }

    private static bool TryGetValidGamePath(out DirectoryInfo gamePath, out string errorMessage)
    {
        if (App.Settings.GamePath == null || !App.Settings.GamePath.Exists)
        {
            gamePath     = null!;
            errorMessage = "请先选择游戏目录";
            return false;
        }

        gamePath     = App.Settings.GamePath;
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryGetGamePath(out DirectoryInfo gamePath, out string errorMessage)
    {
        if (App.Settings.GamePath == null || string.IsNullOrWhiteSpace(App.Settings.GamePath.FullName))
        {
            gamePath     = null!;
            errorMessage = "请先选择游戏目录";
            return false;
        }

        gamePath     = App.Settings.GamePath;
        errorMessage = string.Empty;
        return true;
    }

    private GameClientFileTaskWindow CreateWindow(GameClientFileTaskWindowViewModel viewModel) =>
        window.Dispatcher.Invoke
        (() =>
            {
                var dialog = new GameClientFileTaskWindow(viewModel);

                if (window.IsVisible)
                {
                    dialog.Owner         = window;
                    dialog.ShowInTaskbar = false;
                }

                return dialog;
            }
        );

    private static void ShowWindow(GameClientFileTaskWindow dialog) =>
        dialog.Dispatcher.Invoke
        (() =>
            {
                dialog.Show();
                dialog.Activate();
            }
        );

    private static void CloseWindow(GameClientFileTaskWindow dialog)
    {
        if (!dialog.Dispatcher.CheckAccess())
        {
            dialog.Dispatcher.Invoke(() => CloseWindow(dialog));
            return;
        }

        dialog.Close();
    }

    private static string FormatEstimatedTime(long remaining, long speed)
    {
        if (speed <= 0)
            return string.Empty;

        var remainingSeconds = (int)Math.Ceiling((double)remaining / speed);
        remainingSeconds = Math.Clamp(remainingSeconds, 0, (60 * 60 * 100) - 1);
        if (remainingSeconds < 60 * 60)
            return $"{remainingSeconds / 60:00}:{remainingSeconds % 60:00}";

        return $"{remainingSeconds / 60 / 60:00}:{remainingSeconds / 60 % 60:00}:{remainingSeconds % 60:00}";
    }
}
