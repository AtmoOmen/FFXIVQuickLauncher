using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Game.Integrity;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.V3;
using XIVLauncher.Common.Game.Update;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.Common.Patching.SdoFileDownload;
using XIVLauncher.Common.Runtime;
using XIVLauncher.Common.Util;
using XIVLauncher.PlatformAbstractions;
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
            _                                     => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知客户端文件任务类型")
        };

    private async Task<GameClientFileTaskResult> RunUpdateAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "更新游戏文件";

        Log.Information("[GameClientFileTask] 开始更新流程");

        if (!TryGetValidGamePath(out var gamePath, out var gamePathError))
        {
            Log.Warning("[GameClientFileTask] 游戏路径无效: {Message}", gamePathError);
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, gamePathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
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

        LoginResult loginResult;

        try
        {
            Log.Information("[GameClientFileTask] 正在检查游戏更新");
            loginResult = await UpdateClient.Check(gamePath, false, cancellationTokenSource.Token).ConfigureAwait(false);
            Log.Information("[GameClientFileTask] 更新检查完成, 状态 {State}, 存在 V3 计划 {HasV3Plan}", loginResult.State, loginResult.V3GameUpdatePlan != null);
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

        if (loginResult.State != LoginState.NeedsPatchGame)
            return await WaitForCloseAsync(viewModel, CreateSuccessSnapshot(TITLE, "更新检查已完成", "当前没有待安装的更新内容"), GameClientFileTaskResultStatus.Success).ConfigureAwait(false);

        if (loginResult.V3GameUpdatePlan == null)
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "获取游戏更新计划失败"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        return await RunV3GamePatchUpdateAsync(viewModel, loginResult.V3GameUpdatePlan).ConfigureAwait(false);
    }

    private async Task<GameClientFileTaskResult> RunV3GamePatchUpdateAsync(GameClientFileTaskWindowViewModel viewModel, V3GameUpdatePlan updatePlan)
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

        var progress = new Progress<V3GamePatchProgress>(value => ApplySnapshot(viewModel, CreateV3GamePatchSnapshot(value)));

        try
        {
            var assemblyLocation      = AppContext.BaseDirectory;
            var workerExecutablePath  = Path.Combine(assemblyLocation, "PatchInstaller", "XIVLauncher.PatchInstaller.exe");
            var adminAccessRequired   = PatchVerifier.AdminAccessRequired(App.Settings.GamePath.FullName);
            var patchInstallerRuntime = DotNetRuntimeManager.GetRuntimeDirectory("win-x86");

            ApplySnapshot(viewModel, CreateRunningSnapshot(TITLE, "正在准备补丁安装器运行时"));
            var runtimeVersion = await DotNetRuntimeManager.GetLatestVersionAsync(cancellationTokenSource.Token).ConfigureAwait(false);
            await DotNetRuntimeManager.EnsureRuntimeAsync
                                      (
                                          patchInstallerRuntime,
                                          runtimeVersion,
                                          "win-x86",
                                          "补丁安装器 .NET 运行时",
                                          message => ApplySnapshot(viewModel,                CreateRunningSnapshot(TITLE, message)),
                                          (total, downloaded, _) => ApplySnapshot(viewModel, CreateRuntimeDownloadSnapshot(TITLE, total, downloaded)),
                                          cancellationTokenSource.Token
                                      )
                                      .ConfigureAwait(false);

            Log.Information
            (
                "[GameClientFileTask] 准备安装 V3 更新, 当前 {CurrentGameVersion}/{CurrentDataVersion}, 目标 {TargetGameVersion}/{TargetDataVersion}, 包数量 {PackageCount}, 提权 {AdminAccessRequired}, Worker {WorkerPath}",
                updatePlan.CurrentGameVersion,
                updatePlan.CurrentDataVersion,
                updatePlan.TargetGameVersion,
                updatePlan.TargetDataVersion,
                updatePlan.Packages.Count,
                adminAccessRequired,
                workerExecutablePath
            );

            using var fileInstaller = new SdoFileDownloadRemoteInstaller
            (
                workerExecutablePath,
                adminAccessRequired,
                patchInstallerRuntime.FullName
            );
            using var patchInstaller = new V3GamePatchInstaller();

            await patchInstaller.InstallAsync
                                (
                                    updatePlan,
                                    App.Settings.GamePath,
                                    App.Settings.PatchPath,
                                    fileInstaller,
                                    App.Settings.KeepPatches,
                                    TimeSpan.FromMilliseconds(100),
                                    progress,
                                    cancellationTokenSource.Token
                                )
                                .ConfigureAwait(false);

            Log.Information("[GameClientFileTask] V3 游戏更新安装完成");
            return await WaitForCloseAsync(viewModel, CreateSuccessSnapshot(TITLE, "游戏更新已完成", "所有更新内容已安装完成"), GameClientFileTaskResultStatus.Success).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(TITLE, "已取消更新"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[GameClientFileTask] V3 游戏更新失败");
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "游戏更新失败", ex.Message), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
        }
    }

    private async Task<GameClientFileTaskResult> RunRepairAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "修复游戏文件";

        if (!TryGetValidGamePath(out _, out var gamePathError))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, gamePathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (!TryResolvePatchPath(out var patchPathError))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, patchPathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (GameHelpers.CheckIsGameOpen())
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "官方启动器或游戏正在运行", "请关闭相关进程后重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        return await RunPatchVerifierAsync(viewModel, PatchVerifierMode.Repair).ConfigureAwait(false);
    }

    private async Task<GameClientFileTaskResult> RunIntegrityCheckAsync(GameClientFileTaskWindowViewModel viewModel)
    {
        const string TITLE = "检查游戏完整性";

        if (!TryGetValidGamePath(out var gamePath, out var gamePathError))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, gamePathError), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        using var cancellationTokenSource = new CancellationTokenSource();
        var       ctsBox                  = new StrongBox<CancellationTokenSource?>(cancellationTokenSource);
        SetRunningHandler(viewModel, () => ctsBox.Value?.Cancel());
        ApplySnapshot(viewModel, CreateRunningSnapshot(TITLE, "正在获取完整性参考数据"));

        IntegrityCheckCompareOutcome outcome;

        try
        {
            var progress = new Progress<IntegrityCheckProgress>(value => ApplySnapshot(viewModel, CreateIntegrityCheckRunningSnapshot(TITLE, value)));
            outcome = await IntegrityCheck.CompareIntegrityAsync(progress, gamePath, false, cancellationTokenSource.Token).ConfigureAwait(false);
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
            case IntegrityCheckCompareResult.ReferenceNotFound:
                return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(TITLE, "当前游戏版本还没有可用的参考报告", "请稍后再试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

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

    private async Task<GameClientFileTaskResult> RunPatchVerifierAsync(GameClientFileTaskWindowViewModel viewModel, PatchVerifierMode mode)
    {
        var title      = mode == PatchVerifierMode.Update ? "更新游戏文件" : "修复游戏文件";
        var actionText = mode == PatchVerifierMode.Update ? "更新" : "修复";

        using var mutex = new Mutex(false, "XivLauncherIsPatching");

        if (!mutex.WaitOne(0, false))
            return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(title, "另一实例正在执行游戏更新", "请关闭其他 XIVLauncher 实例后重试"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);

        if (!AppUtil.TryYellOnGameFilesBeingOpen(window, _ => $"关闭以下进程以{actionText}游戏"))
            return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(title, $"已取消{actionText}"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);

        while (true)
        {
            using var verify                      = new PatchVerifier(CommonSettings.Instance, mode, TimeSpan.FromMilliseconds(100));
            using var pollCancellationTokenSource = new CancellationTokenSource();
            var       verifyBox                   = new StrongBox<PatchVerifier?>(verify);

            var cancellationRequested = false;
            SetRunningHandler
            (
                viewModel,
                () =>
                {
                    if (cancellationRequested)
                        return;

                    var currentVerify = verifyBox.Value;
                    if (currentVerify == null)
                        return;

                    cancellationRequested = true;
                    ApplySnapshot(viewModel, CreateDisabledCancelSnapshot(CreatePatchVerifierSnapshot(mode, currentVerify)));
                    _ = currentVerify.Cancel();
                }
            );

            var pollingTask = PollPatchVerifierAsync(viewModel, mode, verify, pollCancellationTokenSource.Token);

            verify.Start();
            await verify.WaitForCompletion().ConfigureAwait(false);

            pollCancellationTokenSource.Cancel();
            await AwaitPollingTaskAsync(pollingTask).ConfigureAwait(false);

            switch (verify.State)
            {
                case PatchVerifier.VerifyState.Done when mode == PatchVerifierMode.Update:
                    return await WaitForCloseAsync(viewModel, CreateSuccessSnapshot(title, "游戏更新已完成", "所有更新内容已安装完成"), GameClientFileTaskResultStatus.Success).ConfigureAwait(false);

                case PatchVerifier.VerifyState.Done:
                {
                    var action = await WaitForChoiceAsync(viewModel, CreateRepairCompletedSnapshot(verify)).ConfigureAwait(false);

                    if (action == GameClientFileTaskWindowAction.Primary)
                        return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Success, ShouldLaunchGame = true };

                    return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Success };
                }

                case PatchVerifier.VerifyState.Error:
                {
                    var action = await WaitForChoiceAsync(viewModel, CreatePatchVerifierFailureSnapshot(mode, verify)).ConfigureAwait(false);
                    if (action == GameClientFileTaskWindowAction.Primary)
                        continue;

                    return new GameClientFileTaskResult { Status = GameClientFileTaskResultStatus.Failed };
                }

                case PatchVerifier.VerifyState.Cancelled:
                    return await WaitForCloseAsync(viewModel, CreateCancelledSnapshot(title, $"已取消{actionText}"), GameClientFileTaskResultStatus.Cancelled).ConfigureAwait(false);

                default:
                    return await WaitForCloseAsync(viewModel, CreateFailureSnapshot(title, $"{actionText}失败", "任务未能正常完成"), GameClientFileTaskResultStatus.Failed).ConfigureAwait(false);
            }
        }
    }

    private async Task PollPatchVerifierAsync(GameClientFileTaskWindowViewModel viewModel, PatchVerifierMode mode, PatchVerifier verify, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ApplySnapshot(viewModel, CreatePatchVerifierSnapshot(mode, verify));
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static GameClientFileTaskSnapshot CreatePatchVerifierSnapshot(PatchVerifierMode mode, PatchVerifier verify)
    {
        var title       = mode == PatchVerifierMode.Update ? "更新游戏文件" : "修复游戏文件";
        var listLabel   = mode == PatchVerifierMode.Update ? "更新清单" : "修复清单";
        var verifyLabel = mode == PatchVerifierMode.Update ? "校验" : "验证";
        var applyLabel  = mode == PatchVerifierMode.Update ? "更新" : "修复";

        return verify.State switch
        {
            PatchVerifier.VerifyState.DownloadMeta => new GameClientFileTaskSnapshot
            {
                Title                   = title,
                PhaseText               = $"正在获取{listLabel}",
                DetailText              = verify.CurrentFile,
                Progress                = verify.Total == 0 ? 0 : 100.0 * verify.Progress / verify.Total,
                IsProgressIndeterminate = false,
                StatusText              = $"{APIHelper.BytesToString(verify.Progress)}/{APIHelper.BytesToString(verify.Total)}",
                SpeedText               = $"{APIHelper.BytesToString(verify.Speed)}/s",
                EtaText                 = FormatEstimatedTime(verify.Total - verify.Progress, verify.Speed),
                PrimaryButtonText       = "取消",
                IsPrimaryButtonVisible  = true,
                IsPrimaryButtonEnabled  = true,
                IsRunning               = true
            },
            PatchVerifier.VerifyState.VerifyAndRepair => new GameClientFileTaskSnapshot
            {
                Title                   = title,
                PhaseText               = verify.CurrentMetaInstallState == IndexedZiPatchInstaller.InstallTaskState.NotStarted ? $"正在{verifyLabel}游戏文件" : $"正在{applyLabel}游戏文件",
                DetailText              = verify.CurrentFile,
                Progress                = verify.Total == 0 ? 0 : 100.0 * verify.Progress / verify.Total,
                IsProgressIndeterminate = false,
                StatusText =
                    $"{Math.Min(verify.TaskIndex + 1, verify.TaskCount)}/{verify.TaskCount} - {APIHelper.BytesToString(verify.Progress)}/{APIHelper.BytesToString(verify.Total)}",
                SpeedText              = GetPatchVerifierSpeedText(verify),
                EtaText                = GetPatchVerifierEtaText(verify),
                Items                  = verify.IsDownloading ? GetPatchVerifierItems(verify) : [],
                PrimaryButtonText      = "取消",
                IsPrimaryButtonVisible = true,
                IsPrimaryButtonEnabled = true,
                IsRunning              = true
            },
            _ => new GameClientFileTaskSnapshot
            {
                Title                   = title,
                PhaseText               = $"正在准备{applyLabel}任务",
                Progress                = verify.State == PatchVerifier.VerifyState.Done ? 100 : 0,
                IsProgressIndeterminate = verify.State != PatchVerifier.VerifyState.Done,
                StatusText              = verify.TaskCount == 0 ? string.Empty : $"{Math.Min(verify.TaskIndex + 1, verify.TaskCount)}/{verify.TaskCount}",
                PrimaryButtonText       = "取消",
                IsPrimaryButtonVisible  = true,
                IsPrimaryButtonEnabled  = true,
                IsRunning               = true
            }
        };
    }

    private static GameClientFileTaskSnapshot CreateV3GamePatchSnapshot(V3GamePatchProgress progress)
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

    private static IReadOnlyList<GameClientFileTaskItemSnapshot> GetPatchVerifierItems(PatchVerifier verify) =>
        verify.GetCurrentInstallProgressEntries()
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

    private static string GetPatchVerifierSpeedText(PatchVerifier verify) =>
        verify.CurrentMetaInstallState switch
        {
            IndexedZiPatchInstaller.InstallTaskState.WaitingForReattempt => "请等待后重试",
            IndexedZiPatchInstaller.InstallTaskState.Connecting          => "正在连接",
            IndexedZiPatchInstaller.InstallTaskState.Finishing           => "正在结束",
            _                                                            => $"{APIHelper.BytesToString(verify.Speed)}/s"
        };

    private static string GetPatchVerifierEtaText(PatchVerifier verify) =>
        verify.CurrentMetaInstallState switch
        {
            IndexedZiPatchInstaller.InstallTaskState.WaitingForReattempt => string.Empty,
            IndexedZiPatchInstaller.InstallTaskState.Connecting          => string.Empty,
            IndexedZiPatchInstaller.InstallTaskState.Finishing           => string.Empty,
            _                                                            => FormatEstimatedTime(verify.Total - verify.Progress, verify.Speed)
        };

    private static GameClientFileTaskSnapshot CreateRepairCompletedSnapshot(PatchVerifier verify)
    {
        var detailText = verify.NumBrokenFiles switch
        {
            0 => "未检测到任何损坏的游戏文件",
            _ => $"已成功修复 {verify.NumBrokenFiles} 个游戏文件"
        };

        if (verify.MovedFiles.Count != 0)
            detailText = $"{detailText}\n已将 {verify.MovedFiles.Count} 个非原始游戏文件移动至\n{verify.MovedFileToDir}";

        return new GameClientFileTaskSnapshot
        {
            Title                  = "修复游戏文件",
            PhaseText              = "修复已完成",
            DetailText             = detailText,
            StatusText             = verify.MovedFiles.Count == 0 ? string.Empty : string.Join("\n", verify.MovedFiles.Select(file => $"* {file}")),
            PrimaryButtonText      = "启动游戏",
            IsPrimaryButtonVisible = true,
            IsPrimaryButtonEnabled = true,
            CloseButtonText        = "关闭",
            IsCloseButtonVisible   = true,
            IsCloseButtonEnabled   = true
        };
    }

    private static GameClientFileTaskSnapshot CreatePatchVerifierFailureSnapshot(PatchVerifierMode mode, PatchVerifier verify)
    {
        var actionText = mode == PatchVerifierMode.Update ? "更新" : "修复";
        var detailText = verify.LastException switch
        {
            null                                                                                    => $"{actionText}失败: 未知错误, 可能需要重新安装游戏",
            _ when verify.LastException.ToString().Contains("Data error", StringComparison.Ordinal) => $"{actionText}失败: 检查游戏文件过程中硬盘报错, 可能存在物理故障",
            _                                                                                       => $"{actionText}失败: {verify.LastException.Message}"
        };

        return new GameClientFileTaskSnapshot
        {
            Title                  = mode == PatchVerifierMode.Update ? "更新游戏文件" : "修复游戏文件",
            PhaseText              = $"{actionText}未完成",
            DetailText             = detailText,
            StatusText             = verify.LastException?.GetType().Name ?? string.Empty,
            PrimaryButtonText      = "重试",
            IsPrimaryButtonVisible = true,
            IsPrimaryButtonEnabled = true,
            CloseButtonText        = "关闭",
            IsCloseButtonVisible   = true,
            IsCloseButtonEnabled   = true
        };
    }

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

    private static GameClientFileTaskSnapshot CreateRunningSnapshot(string title, string phaseText) =>
        new()
        {
            Title                   = title,
            PhaseText               = phaseText,
            IsProgressIndeterminate = true,
            PrimaryButtonText       = "取消",
            IsPrimaryButtonVisible  = true,
            IsPrimaryButtonEnabled  = true,
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

        if (Repository.Ffxiv.IsBaseVer(App.Settings.GamePath))
        {
            gamePath     = null!;
            errorMessage = "所选路径中没有检测到游戏安装";
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
        remainingSeconds = Math.Clamp(remainingSeconds, 0, 60 * 60 * 100 - 1);
        if (remainingSeconds < 60 * 60)
            return $"{remainingSeconds / 60:00}:{remainingSeconds % 60:00}";

        return $"{remainingSeconds / 60 / 60:00}:{remainingSeconds / 60 % 60:00}:{remainingSeconds % 60:00}";
    }
}
