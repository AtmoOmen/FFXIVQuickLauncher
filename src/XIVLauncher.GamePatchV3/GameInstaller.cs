using System.Diagnostics;
using Serilog;

namespace XIVLauncher.GamePatchV3;

public sealed class GameInstaller
{
    public long    Speed                     { get; private set; }
    public int     TaskIndex                 { get; private set; }
    public long    Progress                  { get; private set; }
    public long    Total                     { get; private set; }
    public int     TaskCount                 { get; private set; }
    public string  CurrentFile               { get; private set; } = string.Empty;
    public SdoFileDownloader.InstallTaskState CurrentMetaInstallState { get; private set; } = SdoFileDownloader.InstallTaskState.NotStarted;
    public InstallState State                { get; private set; } = InstallState.NotStarted;

    private readonly string   gamePath;
    private readonly TimeSpan progressUpdateInterval;
    private readonly List<Tuple<long, long>> reportedProgresses = [];
    private CancellationTokenSource cts = new();

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

            var targetRelativePaths = remoteIntegrity.Hashes
                                                      .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                                                      .Select(x => NormalizeSdoTargetRelativePath(x.Key))
                                                      .ToList();

            using var downloader = CreateAndConfigureDownloader();
            var installProgressTaskIndex = 0;

            void UpdateInstallProgress(int sourceIndex, long progress, long max, SdoFileDownloader.InstallTaskState state)
            {
                if (targetRelativePaths.Count <= 0)
                    return;

                CurrentFile = targetRelativePaths[Math.Min(sourceIndex, targetRelativePaths.Count - 1)];
                if (state == SdoFileDownloader.InstallTaskState.Complete)
                    TaskIndex = Interlocked.Increment(ref installProgressTaskIndex);
                Progress = Math.Min(progress, max);
                Total    = max;
                CurrentMetaInstallState = state switch
                {
                    SdoFileDownloader.InstallTaskState.Connecting  => SdoFileDownloader.InstallTaskState.Connecting,
                    SdoFileDownloader.InstallTaskState.Downloading => SdoFileDownloader.InstallTaskState.Downloading,
                    SdoFileDownloader.InstallTaskState.Complete    => SdoFileDownloader.InstallTaskState.Complete,
                    _                                               => SdoFileDownloader.InstallTaskState.NotStarted
                };
                RecordProgressForEstimation();
            }

            downloader.OnInstallProgress += UpdateInstallProgress;

            try
            {
                downloader.ConstructFromRemoteIntegrity(remoteIntegrity);

                TaskCount     = targetRelativePaths.Count;
                State         = InstallState.Installing;
                CurrentMetaInstallState = SdoFileDownloader.InstallTaskState.Connecting;

                for (var fileIndex = 0; fileIndex < targetRelativePaths.Count; fileIndex++)
                {
                    var filePath = targetRelativePaths[fileIndex];
                    var sdoPath  = $"\\game\\{filePath.Replace('/', '\\')}";
                    downloader.QueueInstall(fileIndex, sdoPath);
                }

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

    public void Cancel()
    {
        cts.Cancel();
    }

    private SdoFileDownloader CreateAndConfigureDownloader()
    {
        return new()
        {
            ProgressReportInterval = progressUpdateInterval.TotalMilliseconds > 0 ? (int)progressUpdateInterval.TotalMilliseconds : 250
        };
    }

    private void RecordProgressForEstimation()
    {
        var now = DateTime.Now.Ticks;
        reportedProgresses.Add(Tuple.Create(now, Progress));
        while (now - reportedProgresses.First().Item1 > 10 * 1000 * 8000)
            reportedProgresses.RemoveAt(0);

        var elapsedMs = reportedProgresses.Last().Item1 - reportedProgresses.First().Item1;
        Speed = elapsedMs == 0 ? 0 : (reportedProgresses.Last().Item2 - reportedProgresses.First().Item2) * 10 * 1000 * 1000 / elapsedMs;
    }

    private static string NormalizeSdoTargetRelativePath(string path)
    {
        var normalized = path.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar)["\\game\\".Length..];
        return normalized;
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
