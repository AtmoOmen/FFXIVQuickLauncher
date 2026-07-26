using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Serilog;
using XIVLauncher.GamePatchV3.Integrity;
using XIVLauncher.GamePatchV3.Integrity.Models;

namespace XIVLauncher.GamePatchV3.Repair;

public sealed class GameRepairer
(
    string   gamePath,
    TimeSpan progressUpdateInterval
)
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

    public long                                Speed                         => speedEstimator.Speed;
    public int                                 TaskIndex                     { get; private set; }
    public long                                Progress                      { get; private set; }
    public long                                Total                         { get; private set; }
    public int                                 TaskCount                     { get; private set; }
    public string                              CurrentFile                   { get; private set; } = string.Empty;
    public int                                 NumBrokenFiles                { get; private set; }
    public List<string>                        MovedFiles                    { get; }              = [];
    public string                              MovedFileToDir                { get; private set; } = string.Empty;
    public GameFileDownloader.InstallTaskState CurrentMetaInstallState       { get; private set; } = GameFileDownloader.InstallTaskState.NotStarted;
    public int                                 CurrentInstallBrokenFileCount { get; private set; }
    public bool                                IsDownloading                 { get; private set; }
    public RepairState                         State                         { get; private set; } = RepairState.NotStarted;

    private readonly ConcurrentDictionary<int, InstallProgressEntry> currentInstallProgressBySourceIndex = new();
    private readonly TransferSpeedEstimator                          speedEstimator                      = new();
    private          CancellationTokenSource                         cts                                 = new();

    public Dictionary<int, InstallProgressEntry> GetCurrentInstallProgressEntries() =>
        currentInstallProgressBySourceIndex.ToDictionary(x => x.Key, x => x.Value);

    public async Task RunAsync
    (
        CancellationToken cancellationToken = default
    )
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        try
        {
            State = RepairState.DownloadMeta;
            var remoteIntegrity = await GameIntegrityChecker.DownloadIntegrityCheckForVersion(token).ConfigureAwait(false);
            var repairTargets   = IntegrityPathEntry.BuildEntries(remoteIntegrity);
            var targetRelativePaths = repairTargets
                                      .Select(x => x.LocalRelativePath)
                                      .ToList();
            var fileBroken = Enumerable.Repeat(false, targetRelativePaths.Count).ToList();

            using var downloader = new GameFileDownloader();
            downloader.ProgressReportInterval = progressUpdateInterval.TotalMilliseconds > 0 ?
                                                    (int)progressUpdateInterval.TotalMilliseconds :
                                                    250;
            var installProgressTaskIndex = 0;

            void UpdateVerifyProgress
            (
                int  targetIndex,
                int  count,
                long progress,
                long max
            )
            {
                if (targetRelativePaths.Count <= 0)
                    return;

                CurrentFile = targetRelativePaths[Math.Min(targetIndex, targetRelativePaths.Count - 1)];
                TaskIndex   = count;
                Progress    = Math.Min(progress, max);
                Total       = max;
                speedEstimator.Update(Progress);
            }

            void UpdateInstallProgress
            (
                int                                 sourceIndex,
                long                                fileProgress,
                long                                fileTotal,
                long                                totalProgress,
                long                                total,
                GameFileDownloader.InstallTaskState state
            )
            {
                if (targetRelativePaths.Count <= 0)
                    return;

                CurrentFile = targetRelativePaths[Math.Min(sourceIndex, targetRelativePaths.Count - 1)];
                if (state == GameFileDownloader.InstallTaskState.Complete)
                    TaskIndex = Interlocked.Increment(ref installProgressTaskIndex);
                Progress = Math.Min(totalProgress, total);
                Total    = total;
                CurrentMetaInstallState = state switch
                {
                    GameFileDownloader.InstallTaskState.Connecting  => GameFileDownloader.InstallTaskState.Connecting,
                    GameFileDownloader.InstallTaskState.Downloading => GameFileDownloader.InstallTaskState.Downloading,
                    GameFileDownloader.InstallTaskState.Complete    => GameFileDownloader.InstallTaskState.Complete,
                    _                                               => GameFileDownloader.InstallTaskState.NotStarted
                };
                UpdateInstallProgressEntry(sourceIndex, CurrentFile, fileProgress, fileTotal);
                speedEstimator.Update(totalProgress);
            }

            downloader.OnVerifyProgress  += UpdateVerifyProgress;
            downloader.OnInstallProgress += UpdateInstallProgress;

            try
            {
                downloader.Construct(repairTargets, remoteIntegrity.BaseUrl, remoteIntegrity.DataVersion);

                State = RepairState.Repairing;

                TaskCount               = targetRelativePaths.Count;
                CurrentMetaInstallState = GameFileDownloader.InstallTaskState.NotStarted;

                const int REATTEMPT_COUNT = 5;
                var       repaired        = false;

                for (var attemptIndex = 0; attemptIndex < REATTEMPT_COUNT; attemptIndex++)
                {
                    CurrentMetaInstallState = GameFileDownloader.InstallTaskState.NotStarted;
                    Progress                = Total = TaskIndex = 0;
                    speedEstimator.Reset();

                    await downloader.VerifyFiles(gamePath, attemptIndex > 0, Math.Min(Math.Max(Environment.ProcessorCount, 1), 4), token).ConfigureAwait(false);

                    var brokenFiles = downloader.GetBrokenFiles();
                    speedEstimator.Reset();
                    TaskIndex = 0;
                    TaskCount = brokenFiles.Count;

                    if (!(repaired = brokenFiles.Count == 0))
                    {
                        var brokenFileSet = new HashSet<string>(brokenFiles, StringComparer.OrdinalIgnoreCase);
                        CurrentInstallBrokenFileCount = brokenFileSet.Count;
                        ResetInstallProgressDisplay();

                        for (var brokenFileIndex = 0; brokenFileIndex < targetRelativePaths.Count; brokenFileIndex++)
                        {
                            var repairTarget = repairTargets[brokenFileIndex];
                            if (!brokenFileSet.Contains(repairTarget.CanonicalSdoPath))
                                continue;

                            fileBroken[brokenFileIndex] = true;
                            UpdateInstallProgressEntry(brokenFileIndex, repairTarget.LocalRelativePath, 0, 0);
                            downloader.QueueInstall(brokenFileIndex, repairTarget.DownloadPath);
                        }

                        CurrentMetaInstallState = GameFileDownloader.InstallTaskState.Connecting;
                        await downloader.Install(gamePath, Math.Clamp(Environment.ProcessorCount, 8, 16), token).ConfigureAwait(false);
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
            MoveUnnecessaryFiles(gameRootPath, targetRelativePaths, token);

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

    public void Cancel() =>
        cts.Cancel();

    private void MoveUnnecessaryFiles
    (
        string                      path,
        IReadOnlyCollection<string> targetRelativePaths,
        CancellationToken           cancellationToken
    )
    {
        MovedFileToDir = Path.Combine(path, "repair_recycler", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));

        var rootPathInfo = new DirectoryInfo(path);
        path = rootPathInfo.FullName;
        var targetFiles       = targetRelativePaths.Select(NormalizeRelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetDirectories = BuildTargetDirectories(targetFiles);

        Queue<DirectoryInfo> directoriesToVisit = new();
        directoriesToVisit.Enqueue(rootPathInfo);

        while (directoriesToVisit.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = directoriesToVisit.Dequeue();

            var relativeDirPath = dir == rootPathInfo ?
                                      string.Empty :
                                      GetRelativePath(path, dir.FullName);
            if (ShouldIgnore(relativeDirPath))
                continue;

            foreach (var subdir in dir.GetDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((subdir.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var relativePath = GetRelativePath(path, subdir.FullName) + "/";
                if (ShouldIgnore(relativePath))
                    continue;

                if (!targetDirectories.Contains(relativePath))
                {
                    MoveFileToRecycler(subdir.FullName, Path.Combine(MovedFileToDir, relativePath));
                    MovedFiles.Add(relativePath);
                }
                else
                    directoriesToVisit.Enqueue(subdir);
            }

            foreach (var file in dir.GetFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = GetRelativePath(path, file.FullName);
                if (targetFiles.Contains(relativePath))
                    continue;

                if (ShouldIgnore(relativePath))
                    continue;

                MoveFileToRecycler(file.FullName, Path.Combine(MovedFileToDir, relativePath));
                MovedFiles.Add(relativePath);
            }
        }
    }

    private static HashSet<string> BuildTargetDirectories
    (
        IEnumerable<string> targetFiles
    )
    {
        var targetDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var targetFile in targetFiles)
        {
            var separatorIndex = targetFile.LastIndexOf('/');

            while (separatorIndex >= 0)
            {
                targetDirectories.Add(targetFile[..(separatorIndex + 1)]);
                separatorIndex = targetFile.LastIndexOf('/', separatorIndex - 1);
            }
        }

        return targetDirectories;
    }

    private static string NormalizeRelativePath
    (
        string path
    ) =>
        path.TrimStart('\\', '/').Replace('\\', '/');

    private static string GetRelativePath
    (
        string rootPath,
        string fullPath
    ) =>
        NormalizeRelativePath(Path.GetRelativePath(rootPath, fullPath));

    private static bool ShouldIgnore
    (
        string relativePath
    ) =>
        GameIgnoreUnnecessaryFilePatterns.Any(pattern => pattern.IsMatch(relativePath));

    private static void MoveFileToRecycler
    (
        string source,
        string target
    )
    {
        if (File.Exists(source))
        {
            var fileTargetDir = Path.GetDirectoryName(target) ?? throw new InvalidOperationException();
            Directory.CreateDirectory(fileTargetDir);
            File.Move(source, target);
            return;
        }

        if (!Directory.Exists(source))
            return;

        var normalizedTarget = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetParentDir  = Path.GetDirectoryName(normalizedTarget) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(targetParentDir);

        var sourceParentDir = Path.GetDirectoryName(source) ?? throw new InvalidOperationException();
        Directory.Move(source, normalizedTarget);
        if (Directory.GetFileSystemEntries(sourceParentDir).Length == 0)
            Directory.Delete(sourceParentDir);
    }

    private void ResetInstallProgressDisplay()
    {
        IsDownloading = false;
        currentInstallProgressBySourceIndex.Clear();
    }

    private void UpdateInstallProgressEntry
    (
        int    sourceIndex,
        string filePath,
        long   progress,
        long   total
    )
    {
        IsDownloading = true;
        var effectiveProgress = total > 0 ?
                                    Math.Min(progress, total) :
                                    progress;
        currentInstallProgressBySourceIndex[sourceIndex] = new InstallProgressEntry(filePath, effectiveProgress, total);
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

    public static bool AdminAccessRequired
    (
        string gameRootPath
    )
    {
        string tempFn;

        do
        {
            tempFn = Path.Combine(gameRootPath, Guid.NewGuid().ToString());
        }
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

    public static List<FileInfo> GetRelevantFiles
    (
        string gamePath
    )
    {
        var rootPathInfo = new DirectoryInfo(gamePath);
        gamePath = rootPathInfo.FullName;

        Queue<DirectoryInfo> directoriesToVisit = new();
        directoriesToVisit.Enqueue(rootPathInfo);

        List<FileInfo> files = [];

        while (directoriesToVisit.Count != 0)
        {
            var dir = directoriesToVisit.Dequeue();

            var relativeDirPath = dir == rootPathInfo ?
                                      string.Empty :
                                      GetRelativePath(gamePath, dir.FullName);
            if (ShouldIgnore(relativeDirPath))
                continue;

            foreach (var subdir in dir.EnumerateDirectories())
            {
                if ((subdir.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                directoriesToVisit.Enqueue(subdir);
            }

            files.AddRange
            (
                from file in dir.EnumerateFiles()
                let relativePath = GetRelativePath(gamePath, file.FullName)
                where !ShouldIgnore(relativePath)
                select file
            );
        }

        return files;
    }

    private static readonly Regex[] GameIgnoreUnnecessaryFilePatterns =
    [
        new(@"^ffxivgame\.(?:bck|ver)$", RegexOptions.IgnoreCase                   | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"^sqpack/ex([1-9][0-9]*)/ex\1\.(?:bck|ver)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"^My Games/.*$", RegexOptions.IgnoreCase                              | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"^Launcher3Configs/.*$", RegexOptions.IgnoreCase                      | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"^repair_recycler/.*$", RegexOptions.IgnoreCase                       | RegexOptions.CultureInvariant | RegexOptions.Compiled)
    ];
}
