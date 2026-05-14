using Serilog;
using XIVLauncher.GamePatchV3.Integrity;
using XIVLauncher.GamePatchV3.Integrity.Models;

namespace XIVLauncher.GamePatchV3.Install;

public sealed class GameInstaller
{
    public long                               Speed                   { get; private set; }
    public int                                TaskIndex               { get; private set; }
    public long                               Progress                { get; private set; }
    public long                               Total                   { get; private set; }
    public int                                TaskCount               { get; private set; }
    public string                             CurrentFile             { get; private set; } = string.Empty;
    public GameFileDownloader.InstallTaskState CurrentMetaInstallState { get; private set; } = GameFileDownloader.InstallTaskState.NotStarted;
    public InstallState                       State                   { get; private set; } = InstallState.NotStarted;

    private readonly string                  gamePath;
    private readonly TimeSpan                progressUpdateInterval;
    private readonly List<Tuple<long, long>> reportedProgresses = [];
    private          CancellationTokenSource cts                = new();

    public GameInstaller(string gamePath, TimeSpan progressUpdateInterval)
    {
        this.gamePath               = gamePath;
        this.progressUpdateInterval = progressUpdateInterval;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        try
        {
            State = InstallState.DownloadMeta;
            var remoteIntegrity = await GameIntegrityChecker.DownloadIntegrityCheckForVersion(token).ConfigureAwait(false);
            var installTargets  = IntegrityPathEntry.BuildEntries(remoteIntegrity);
            var targetRelativePaths = installTargets
                                      .Select(x => x.LocalRelativePath)
                                      .ToList();

            using var downloader = new GameFileDownloader();
            downloader.ProgressReportInterval = progressUpdateInterval.TotalMilliseconds > 0 ? (int)progressUpdateInterval.TotalMilliseconds : 250;
            var installProgressTaskIndex = 0;

            void UpdateInstallProgress(int sourceIndex, long progress, long max, GameFileDownloader.InstallTaskState state)
            {
                if (targetRelativePaths.Count <= 0)
                    return;

                CurrentFile = targetRelativePaths[Math.Min(sourceIndex, targetRelativePaths.Count - 1)];
                if (state == GameFileDownloader.InstallTaskState.Complete)
                    TaskIndex = Interlocked.Increment(ref installProgressTaskIndex);
                Progress = Math.Min(progress, max);
                Total    = max;
                CurrentMetaInstallState = state switch
                {
                    GameFileDownloader.InstallTaskState.Connecting  => GameFileDownloader.InstallTaskState.Connecting,
                    GameFileDownloader.InstallTaskState.Downloading => GameFileDownloader.InstallTaskState.Downloading,
                    GameFileDownloader.InstallTaskState.Complete    => GameFileDownloader.InstallTaskState.Complete,
                    _                                              => GameFileDownloader.InstallTaskState.NotStarted
                };
                RecordProgressForEstimation();
            }

            downloader.OnInstallProgress += UpdateInstallProgress;

            try
            {
                downloader.Construct(installTargets, remoteIntegrity.BaseUrl, remoteIntegrity.DataVersion);

                TaskCount               = targetRelativePaths.Count;
                State                   = InstallState.Installing;
                CurrentMetaInstallState = GameFileDownloader.InstallTaskState.Connecting;

                for (var fileIndex = 0; fileIndex < targetRelativePaths.Count; fileIndex++) downloader.QueueInstall(fileIndex, installTargets[fileIndex].DownloadPath);

                await downloader.Install(gamePath, 8, token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(remoteIntegrity.GameVersion))
                {
                    var gameDir = Path.Combine(gamePath, "game");
                    Directory.CreateDirectory(gameDir);
                    await File.WriteAllTextAsync(Path.Combine(gameDir, "ffxivgame.ver"), remoteIntegrity.GameVersion, token).ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(gameDir, "ffxivgame.bck"), remoteIntegrity.GameVersion, token).ConfigureAwait(false);
                    Log.Information("[GameInstaller] 已写入游戏版本文件, 版本 {GameVersion}", remoteIntegrity.GameVersion);
                }

                State = InstallState.Done;
            }
            finally
            {
                downloader.OnInstallProgress -= UpdateInstallProgress;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || token.IsCancellationRequested)
        {
            State = InstallState.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GameInstaller 发生未预期错误");
            State = InstallState.Error;
        }
    }

    public void Cancel() =>
        cts.Cancel();

    private void RecordProgressForEstimation()
    {
        var now = DateTime.Now.Ticks;
        reportedProgresses.Add(Tuple.Create(now, Progress));
        while (now - reportedProgresses.First().Item1 > 10 * 1000 * 8000)
            reportedProgresses.RemoveAt(0);

        var elapsedMs = reportedProgresses.Last().Item1 - reportedProgresses.First().Item1;
        Speed = elapsedMs == 0 ? 0 : (reportedProgresses.Last().Item2 - reportedProgresses.First().Item2) * 10 * 1000 * 1000 / elapsedMs;
    }

    public enum InstallState
    {
        NotStarted,
        DownloadMeta,
        Installing,
        Done,
        Cancelled,
        Error
    }
}
