using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;

namespace XIVLauncher.GamePatchV3;

public sealed class GameRepairer
{
    public readonly struct InstallProgressEntry
    (
        string filePath,
        long   progress,
        long   total
    )
    {
        public string FilePath { get; } = filePath;
        public long   Progress { get; } = progress;
        public long   Total    { get; } = total;
    }

    public long    Speed                         { get; private set; }
    public int     TaskIndex                     { get; private set; }
    public long    Progress                      { get; private set; }
    public long    Total                         { get; private set; }
    public int     TaskCount                     { get; private set; }
    public string  CurrentFile                   { get; private set; } = string.Empty;
    public int     NumBrokenFiles                { get; private set; }
    public List<string> MovedFiles               { get; } = [];
    public string  MovedFileToDir                { get; private set; } = string.Empty;
    public SdoFileDownloader.InstallTaskState CurrentMetaInstallState { get; private set; } = SdoFileDownloader.InstallTaskState.NotStarted;
    public int     CurrentInstallBrokenFileCount  { get; private set; }
    public bool    IsDownloading                 { get; private set; }
    public RepairState State                     { get; private set; } = RepairState.NotStarted;

    private readonly string   gamePath;
    private readonly TimeSpan progressUpdateInterval;
    private readonly Lock     installProgressLock                 = new();
    private readonly Dictionary<int, InstallProgressEntry> currentInstallProgressBySourceIndex = new();
    private readonly List<Tuple<long, long>> reportedProgresses = [];
    private CancellationTokenSource cts = new();

    public GameRepairer(string gamePath, TimeSpan progressUpdateInterval)
    {
        this.gamePath               = gamePath;
        this.progressUpdateInterval = progressUpdateInterval;
    }

    public Dictionary<int, InstallProgressEntry> GetCurrentInstallProgressEntries()
    {
        lock (installProgressLock)
            return currentInstallProgressBySourceIndex.ToDictionary(x => x.Key, x => x.Value);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        try
        {
            State = RepairState.DownloadMeta;
            var remoteIntegrity = await GameIntegrityChecker.DownloadIntegrityCheckForVersion(token).ConfigureAwait(false);

            var targetRelativePaths = remoteIntegrity.Hashes
                                                      .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                                                      .Select(x => NormalizeSdoTargetRelativePath(x.Key))
                                                      .ToList();
            var fileBroken = Enumerable.Repeat(false, targetRelativePaths.Count).ToList();

            using var downloader = CreateAndConfigureDownloader();
            var installProgressTaskIndex = 0;

            void UpdateVerifyProgress(int targetIndex, int count, long progress, long max)
            {
                if (targetRelativePaths.Count <= 0)
                    return;

                CurrentFile = targetRelativePaths[Math.Min(targetIndex, targetRelativePaths.Count - 1)];
                TaskIndex   = count;
                Progress    = Math.Min(progress, max);
                Total       = max;
                RecordProgressForEstimation();
            }

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
                UpdateInstallProgressEntry(sourceIndex, CurrentFile, progress, max);
                RecordProgressForEstimation();
            }

            downloader.OnVerifyProgress  += UpdateVerifyProgress;
            downloader.OnInstallProgress += UpdateInstallProgress;

            try
            {
                downloader.ConstructFromRemoteIntegrity(remoteIntegrity);

                TaskCount     = targetRelativePaths.Count;
                CurrentMetaInstallState = SdoFileDownloader.InstallTaskState.NotStarted;

                const int REATTEMPT_COUNT = 5;
                var       repaired        = false;

                for (var attemptIndex = 0; attemptIndex < REATTEMPT_COUNT; attemptIndex++)
                {
                    CurrentMetaInstallState = SdoFileDownloader.InstallTaskState.NotStarted;
                    Progress = Total = TaskIndex = 0;
                    reportedProgresses.Clear();

                    await downloader.VerifyFiles(gamePath, attemptIndex > 0, Math.Min(Math.Max(Environment.ProcessorCount - 2, 1), 32), token).ConfigureAwait(false);

                    var brokenFiles = downloader.GetBrokenFiles();
                    reportedProgresses.Clear();
                    TaskIndex = 0;
                    TaskCount = brokenFiles.Count;

                    if (!(repaired = !brokenFiles.Any()))
                    {
                        var brokenFileSet = new HashSet<string>(brokenFiles, StringComparer.OrdinalIgnoreCase);
                        CurrentInstallBrokenFileCount = brokenFileSet.Count;
                        ResetInstallProgressDisplay();

                        for (var brokenFileIndex = 0; brokenFileIndex < targetRelativePaths.Count; brokenFileIndex++)
                        {
                            var filePath = targetRelativePaths[brokenFileIndex];
                            if (!brokenFileSet.Contains($"\\game\\{filePath}"))
                                continue;

                            fileBroken[brokenFileIndex] = true;
                            UpdateInstallProgressEntry(brokenFileIndex, filePath, 0, 0);
                            downloader.QueueInstall(brokenFileIndex, $"\\game\\{filePath}");
                        }

                        CurrentMetaInstallState = SdoFileDownloader.InstallTaskState.Connecting;
                        await downloader.Install(gamePath, 8, token).ConfigureAwait(false);
                        CurrentInstallBrokenFileCount = 0;
                        ResetInstallProgressDisplay();
                        continue;
                    }

                    CurrentInstallBrokenFileCount = 0;
                    ResetInstallProgressDisplay();
                    break;
                }

                if (!repaired)
                    throw new IOException($"修复失败，已尝试 {REATTEMPT_COUNT} 次");

                NumBrokenFiles += fileBroken.Count(x => x);
            }
            finally
            {
                downloader.OnVerifyProgress  -= UpdateVerifyProgress;
                downloader.OnInstallProgress -= UpdateInstallProgress;
            }

            var gameRootPath = Path.Combine(gamePath, "game");
            await MoveUnnecessaryFiles(gameRootPath, [..targetRelativePaths], token).ConfigureAwait(false);

            State = RepairState.Done;
        }
        catch (Exception ex) when (ex is OperationCanceledException || token.IsCancellationRequested)
        {
            State = RepairState.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GameRepairer 发生未预期错误");
            State = RepairState.Error;
        }
        finally
        {
            CurrentInstallBrokenFileCount = 0;
            ResetInstallProgressDisplay();
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

    private async Task MoveUnnecessaryFiles(string gamePath, HashSet<string> targetRelativePaths, CancellationToken cancellationToken)
    {
        MovedFileToDir = Path.Combine(gamePath, "repair_recycler", DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        var rootPathInfo = new DirectoryInfo(gamePath);
        gamePath = rootPathInfo.FullName;

        Queue<DirectoryInfo> directoriesToVisit = new();
        HashSet<DirectoryInfo> directoriesVisited = new();
        directoriesToVisit.Enqueue(rootPathInfo);
        directoriesVisited.Add(rootPathInfo);

        while (directoriesToVisit.Count != 0)
        {
            var dir = directoriesToVisit.Dequeue();

            if (!dir.FullName.ToLowerInvariant().Replace('\\', '/').StartsWith(gamePath.ToLowerInvariant().Replace('\\', '/'), StringComparison.Ordinal))
                continue;

            var relativeDirPath = dir == rootPathInfo ? string.Empty : dir.FullName[(gamePath.Length + 1)..].Replace('\\', '/');
            if (GameIgnoreUnnecessaryFilePatterns.Any(x => x.IsMatch(relativeDirPath)))
                continue;

            if (!dir.EnumerateFileSystemInfos().Any())
            {
                if (Directory.Exists(dir.FullName))
                    Directory.Delete(dir.FullName);
                Directory.CreateDirectory(Path.Combine(MovedFileToDir, relativeDirPath));
                continue;
            }

            foreach (var subdir in dir.EnumerateDirectories())
            {
                if (directoriesVisited.Contains(subdir))
                    continue;

                if (!subdir.FullName.ToLowerInvariant().Replace('\\', '/').StartsWith(gamePath.ToLowerInvariant().Replace('\\', '/'), StringComparison.Ordinal))
                    continue;

                var relativePath = subdir.FullName[(gamePath.Length + 1)..].Replace('\\', '/') + "/";

                if (GameIgnoreUnnecessaryFilePatterns.Any(x => x.IsMatch(relativePath)))
                    continue;

                if (!targetRelativePaths.Any(x => x.TrimStart('\\').Replace('\\', '/').ToLowerInvariant().StartsWith(relativePath.ToLowerInvariant(), StringComparison.Ordinal)))
                {
                    MoveFileToRecycler(subdir.FullName, Path.Combine(MovedFileToDir, relativePath));
                    MovedFiles.Add(relativePath);
                }
                else
                {
                    directoriesVisited.Add(subdir);
                    directoriesToVisit.Enqueue(subdir);
                }
            }

            foreach (var file in dir.EnumerateFiles())
            {
                if (!file.FullName.ToLowerInvariant().Replace('\\', '/').StartsWith(gamePath.ToLowerInvariant().Replace('\\', '/'), StringComparison.Ordinal))
                    continue;

                var relativePath = file.FullName[(gamePath.Length + 1)..].Replace('\\', '/');
                if (targetRelativePaths.Any(x => x.Replace('\\', '/').ToLowerInvariant() == relativePath.ToLowerInvariant()))
                    continue;

                if (GameIgnoreUnnecessaryFilePatterns.Any(x => x.IsMatch(relativePath)))
                    continue;

                MoveFileToRecycler(file.FullName, Path.Combine(MovedFileToDir, relativePath));
                MovedFiles.Add(relativePath);
            }
        }
    }

    private static void MoveFileToRecycler(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? throw new InvalidOperationException());
        if (File.Exists(source))
        {
            File.Move(source, target);
        }
        else if (Directory.Exists(source))
        {
            var sourceParentDir = Path.GetDirectoryName(source) ?? throw new InvalidOperationException();
            Directory.Move(source, target);
            if (Directory.GetFileSystemEntries(sourceParentDir).Length == 0)
                Directory.Delete(sourceParentDir);
        }
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

    private void ResetInstallProgressDisplay()
    {
        IsDownloading = false;
        lock (installProgressLock)
            currentInstallProgressBySourceIndex.Clear();
    }

    private void UpdateInstallProgressEntry(int sourceIndex, string filePath, long progress, long total)
    {
        IsDownloading = true;
        var effectiveProgress = total > 0 ? Math.Min(progress, total) : progress;
        lock (installProgressLock)
            currentInstallProgressBySourceIndex[sourceIndex] = new InstallProgressEntry(filePath, effectiveProgress, total);
    }

    private static string NormalizeSdoTargetRelativePath(string path)
    {
        var normalized = path.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar)["\\game\\".Length..];
        return normalized;
    }

    public enum RepairState
    {
        NotStarted,
        DownloadMeta,
        Repairing,
        Done,
        Cancelled,
        Error
    }

    public static bool AdminAccessRequired(string gameRootPath)
    {
        string tempFn;
        do
            tempFn = Path.Combine(gameRootPath, Guid.NewGuid().ToString());
        while (File.Exists(tempFn));

        try
        {
            File.WriteAllText(tempFn, string.Empty);
            File.Delete(tempFn);
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    public static List<FileInfo> GetRelevantFiles(string gamePath)
    {
        var rootPathInfo = new DirectoryInfo(gamePath);
        gamePath = rootPathInfo.FullName;

        Queue<DirectoryInfo>   directoriesToVisit = new();
        HashSet<DirectoryInfo> directoriesVisited = new();
        directoriesToVisit.Enqueue(rootPathInfo);
        directoriesVisited.Add(rootPathInfo);

        List<FileInfo> files = [];

        while (directoriesToVisit.Count != 0)
        {
            var dir = directoriesToVisit.Dequeue();

            if (!dir.FullName.ToLowerInvariant().Replace('\\', '/').StartsWith(gamePath.ToLowerInvariant().Replace('\\', '/'), StringComparison.Ordinal))
                continue;

            var relativeDirPath = dir == rootPathInfo ? string.Empty : dir.FullName[(gamePath.Length + 1)..].Replace('\\', '/');
            if (GameIgnoreUnnecessaryFilePatterns.Any(x => x.IsMatch(relativeDirPath)))
                continue;

            foreach (var subdir in dir.EnumerateDirectories())
            {
                if (!directoriesVisited.Add(subdir))
                    continue;

                directoriesToVisit.Enqueue(subdir);
            }

            files.AddRange
            (
                from file in dir.EnumerateFiles()
                where file.FullName.ToLowerInvariant().Replace('\\', '/').StartsWith(gamePath.ToLowerInvariant().Replace('\\', '/'), StringComparison.Ordinal)
                let relativePath = file.FullName[(gamePath.Length + 1)..].Replace('\\', '/')
                where !GameIgnoreUnnecessaryFilePatterns.Any(x => x.IsMatch(relativePath))
                select file
            );
        }

        return files;
    }

    private static readonly Regex[] GameIgnoreUnnecessaryFilePatterns =
    [
        new(@"^ffxivgame\.(?:bck|ver)$", RegexOptions.IgnoreCase),
        new(@"^sqpack/ex([1-9][0-9]*)/ex\1\.(?:bck|ver)$", RegexOptions.IgnoreCase),
        new(@"^My Games/.*$", RegexOptions.IgnoreCase),
        new(@"^Launcher3Configs/.*$", RegexOptions.IgnoreCase),
        new(@"^repair_recycler/.*$", RegexOptions.IgnoreCase)
    ];
}
