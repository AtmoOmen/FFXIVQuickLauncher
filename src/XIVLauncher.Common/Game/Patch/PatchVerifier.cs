using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;
using XIVLauncher.Common.Game.Integrity;
using XIVLauncher.Common.Patching;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.Common.Patching.SdoFileDownload;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Runtime;

namespace XIVLauncher.Common.Game.Patch;

public class PatchVerifier : IDisposable
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

    public TimeSpan                                 ProgressUpdateInterval        { get; }
    public PatchVerifierMode                        Mode                          { get; }
    public List<string>                             MovedFiles                    { get; } = new();
    public int                                      NumBrokenFiles                { get; private set; }
    public string                                   MovedFileToDir                { get; private set; } = string.Empty;
    public int                                      PatchSetIndex                 { get; private set; }
    public int                                      PatchSetCount                 { get; private set; }
    public int                                      TaskIndex                     { get; private set; }
    public long                                     Progress                      { get; private set; }
    public long                                     Total                         { get; private set; }
    public int                                      TaskCount                     { get; private set; }
    public IndexedZiPatchInstaller.InstallTaskState CurrentMetaInstallState       { get; private set; } = IndexedZiPatchInstaller.InstallTaskState.NotStarted;
    public string                                   CurrentFile                   { get; private set; }
    public long                                     Speed                         { get; private set; }
    public Exception                                LastException                 { get; private set; }
    public int                                      CurrentInstallBrokenFileCount { get; private set; }
    public bool                                     IsDownloading                 { get; private set; }

    public        VerifyState State { get; private set; } = VerifyState.NotStarted;
    private const string      REPAIR_RECYCLER_DIRECTORY = "repair_recycler";

    private static readonly Regex[] GameIgnoreUnnecessaryFilePatterns =
    [
        // Base game version files.
        new(@"^ffxivgame\.(?:bck|ver)$", RegexOptions.IgnoreCase),

        // Expansion version files.
        new(@"^sqpack/ex([1-9][0-9]*)/ex\1\.(?:bck|ver)$", RegexOptions.IgnoreCase),

        //  Savadata.
        new(@"^My Games/.*$", RegexOptions.IgnoreCase),

        //  shits of Shanda V3 launcher.
        new(@"^Launcher3Configs/.*$", RegexOptions.IgnoreCase),

        // Repair recycle bin folder.
        new(@"^repair_recycler/.*$", RegexOptions.IgnoreCase)
    ];

    private readonly ISettings                             _settings;
    private readonly bool                                  _external;
    private readonly Lock                                  _installProgressLock                 = new();
    private readonly Dictionary<int, InstallProgressEntry> _currentInstallProgressBySourceIndex = new();

    private readonly HttpClient _client;

    private readonly List<Tuple<long, long>> _reportedProgresses      = new();
    private          CancellationTokenSource _cancellationTokenSource = new();

    private Task _verificationTask;

    public PatchVerifier(ISettings settings, PatchVerifierMode mode, TimeSpan progressUpdateInterval, bool external = true)
    {
        _settings              = settings;
        _client                = new HttpClient();
        Mode                   = mode;
        ProgressUpdateInterval = progressUpdateInterval;
        _external              = external;
    }

    #region Disposal

    public void Dispose()
    {
        if (_verificationTask != null && !_verificationTask.IsCompleted)
        {
            _cancellationTokenSource.Cancel();
            _verificationTask.Wait();
        }
    }

    #endregion

    public static List<FileInfo> GetRelevantFiles(string gamePath)
    {
        var rootPathInfo = new DirectoryInfo(gamePath);
        gamePath = rootPathInfo.FullName;

        Queue<DirectoryInfo>   directoriesToVisit = new();
        HashSet<DirectoryInfo> directoriesVisited = new();
        directoriesToVisit.Enqueue(rootPathInfo);
        directoriesVisited.Add(rootPathInfo);

        List<FileInfo> files = new();

        while (directoriesToVisit.Any())
        {
            var dir = directoriesToVisit.Dequeue();

            // For directories, ignore if final path does not belong in the root path.
            if (!dir.FullName.ToLowerInvariant().Replace('\\', '/').StartsWith(gamePath.ToLowerInvariant().Replace('\\', '/'), StringComparison.Ordinal))
                continue;

            var relativeDirPath = dir == rootPathInfo ? "" : dir.FullName.Substring(gamePath.Length + 1).Replace('\\', '/');
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

    public Dictionary<int, InstallProgressEntry> GetCurrentInstallProgressEntries()
    {
        lock (_installProgressLock)
            return _currentInstallProgressBySourceIndex.ToDictionary(x => x.Key, x => x.Value);
    }

    public void Start()
    {
        Debug.Assert(_verificationTask == null || _verificationTask.IsCompleted);

        _cancellationTokenSource = new();
        _reportedProgresses.Clear();
        MovedFiles.Clear();
        NumBrokenFiles                = 0;
        MovedFileToDir                = string.Empty;
        PatchSetIndex                 = 0;
        PatchSetCount                 = 0;
        TaskIndex                     = 0;
        Progress                      = 0;
        Total                         = 0;
        TaskCount                     = 0;
        CurrentFile                   = null;
        Speed                         = 0;
        CurrentMetaInstallState       = IndexedZiPatchInstaller.InstallTaskState.NotStarted;
        LastException                 = null;
        CurrentInstallBrokenFileCount = 0;
        ResetInstallProgressDisplay();

        _verificationTask = Task.Run(RunVerifier, _cancellationTokenSource.Token);
    }

    public Task Cancel()
    {
        _cancellationTokenSource.Cancel();
        return WaitForCompletion();
    }

    public Task WaitForCompletion() => _verificationTask ?? Task.CompletedTask;

    public async Task MoveUnnecessaryFiles(IInstaller installer, string gamePath, HashSet<string> targetRelativePaths)
    {
        MovedFileToDir = Path.Combine(gamePath, REPAIR_RECYCLER_DIRECTORY, DateTime.Now.ToString("yyyyMMdd_HHmmss"));

        var rootPathInfo = new DirectoryInfo(gamePath);
        gamePath = rootPathInfo.FullName;

        Queue<DirectoryInfo>   directoriesToVisit = new();
        HashSet<DirectoryInfo> directoriesVisited = new();
        directoriesToVisit.Enqueue(rootPathInfo);
        directoriesVisited.Add(rootPathInfo);

        while (directoriesToVisit.Count != 0)
        {
            var dir = directoriesToVisit.Dequeue();

            // For directories, ignore if final path does not belong in the root path.
            if (!dir.FullName.ToLowerInvariant().Replace('\\', '/').StartsWith(gamePath.ToLowerInvariant().Replace('\\', '/'), StringComparison.Ordinal))
                continue;

            var relativeDirPath = dir == rootPathInfo ? "" : dir.FullName[(gamePath.Length + 1)..].Replace('\\', '/');
            if (GameIgnoreUnnecessaryFilePatterns.Any(x => x.IsMatch(relativeDirPath)))
                continue;

            if (!dir.EnumerateFileSystemInfos().Any())
            {
                await installer.RemoveDirectory(dir.FullName);
                await installer.CreateDirectory(Path.Combine(MovedFileToDir, relativeDirPath));
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
                    await installer.MoveFile(subdir.FullName, Path.Combine(MovedFileToDir, relativePath));
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

                var relativePath = file.FullName.Substring(gamePath.Length + 1).Replace('\\', '/');
                if (targetRelativePaths.Any(x => x.Replace('\\', '/').ToLowerInvariant() == relativePath.ToLowerInvariant()))
                    continue;

                if (GameIgnoreUnnecessaryFilePatterns.Any(x => x.IsMatch(relativePath)))
                    continue;

                await installer.MoveFile(file.FullName, Path.Combine(MovedFileToDir, relativePath));
                MovedFiles.Add(relativePath);
            }
        }
    }

    private static string NormalizeSdoTargetRelativePath(string path)
    {
        var normalized = path.TrimStart('\\').Replace('\\', Path.DirectorySeparatorChar).Substring("\\game\\".Length - 1);
        return normalized;
    }

    public static bool AdminAccessRequired(string gameRootPath)
    {
        string tempFn;
        do
            tempFn = Path.Combine(gameRootPath, Guid.NewGuid().ToString());
        while (File.Exists(tempFn));

        try
        {
            File.WriteAllText(tempFn, "");
            File.Delete(tempFn);
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        return false;
    }

    private void RecordProgressForEstimation()
    {
        var now = DateTime.Now.Ticks;
        _reportedProgresses.Add(Tuple.Create(now, Progress));
        while (now - _reportedProgresses.First().Item1 > 10 * 1000 * 8000)
            _reportedProgresses.RemoveAt(0);

        var elapsedMs = _reportedProgresses.Last().Item1 - _reportedProgresses.First().Item1;
        if (elapsedMs == 0)
            Speed = 0;
        else
            Speed = (_reportedProgresses.Last().Item2 - _reportedProgresses.First().Item2) * 10 * 1000 * 1000 / elapsedMs;
    }

    private async Task RunVerifier()
    {
        State         = VerifyState.NotStarted;
        LastException = null;
        ISdoFileDownloadInstaller sdoFileInstaller = null;

        try
        {
            var assemblyLocation = AppContext.BaseDirectory;

            if (_external)
            {
                CurrentFile = "补丁安装器运行时";
                var patchInstallerRuntime = DotNetRuntimeManager.GetRuntimeDirectory("win-x86");
                var runtimeVersion        = await DotNetRuntimeManager.GetLatestVersionAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
                await DotNetRuntimeManager.EnsureRuntimeAsync
                                          (
                                              patchInstallerRuntime,
                                              runtimeVersion,
                                              "win-x86",
                                              "补丁安装器 .NET 运行时",
                                              cancellationToken: _cancellationTokenSource.Token
                                          )
                                          .ConfigureAwait(false);

                sdoFileInstaller = new SdoFileDownloadRemoteInstaller
                (
                    Path.Combine(assemblyLocation!, "PatchInstaller", "XIVLauncher.PatchInstaller.exe"),
                    AdminAccessRequired(_settings.GamePath.FullName),
                    patchInstallerRuntime.FullName
                );
            }
            else
                sdoFileInstaller = new SdoFileDownloadLocalInstaller();

            while (!_cancellationTokenSource.IsCancellationRequested && State != VerifyState.Done)
            {
                switch (State)
                {
                    case VerifyState.NotStarted:
                        State = VerifyState.DownloadMeta;
                        break;

                    case VerifyState.DownloadMeta:
                        CurrentFile = "client_all_files_list.dat";
                        Progress    = Total = 0;
                        _reportedProgresses.Clear();
                        State = VerifyState.VerifyAndRepair;
                        break;

                    case VerifyState.VerifyAndRepair:
                    {
                        const int REATTEMPT_COUNT                          = 5;
                        const int MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET = 8;

                        List<string> targetRelativePaths = new();
                        List<bool>   fileBroken          = new();
                        var          repaired            = false;

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

                        var installProgressTaskIndex = 0;

                        void UpdateInstallProgress(int sourceIndex, long progress, long max, SdoFileDownloadInstaller.InstallTaskState state)
                        {
                            if (targetRelativePaths.Count <= 0)
                                return;

                            CurrentFile = targetRelativePaths[Math.Min(sourceIndex, targetRelativePaths.Count - 1)];
                            if (state == SdoFileDownloadInstaller.InstallTaskState.Complete)
                                TaskIndex = Interlocked.Increment(ref installProgressTaskIndex);
                            Progress = Math.Min(progress, max);
                            Total    = max;
                            CurrentMetaInstallState = state switch
                            {
                                SdoFileDownloadInstaller.InstallTaskState.Connecting  => IndexedZiPatchInstaller.InstallTaskState.Connecting,
                                SdoFileDownloadInstaller.InstallTaskState.Downloading => IndexedZiPatchInstaller.InstallTaskState.Working,
                                SdoFileDownloadInstaller.InstallTaskState.Complete    => IndexedZiPatchInstaller.InstallTaskState.Done,
                                _                                                     => IndexedZiPatchInstaller.InstallTaskState.NotStarted
                            };
                            UpdateInstallProgressEntry(sourceIndex, CurrentFile, progress, max);
                            RecordProgressForEstimation();
                        }

                        try
                        {
                            sdoFileInstaller.OnVerifyProgress  += UpdateVerifyProgress;
                            sdoFileInstaller.OnInstallProgress += UpdateInstallProgress;

                            var remoteIntegrity = await IntegrityCheck.DownloadIntegrityCheckForVersion().ConfigureAwait(false);

                            targetRelativePaths = remoteIntegrity.Hashes
                                                                 .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                                                                 .Select(x => NormalizeSdoTargetRelativePath(x.Key))
                                                                 .ToList();
                            await sdoFileInstaller.ConstructFromRemoteIntegrity(remoteIntegrity, ProgressUpdateInterval).ConfigureAwait(false);
                            fileBroken = Enumerable.Repeat(false, targetRelativePaths.Count).ToList();

                            TaskCount               = targetRelativePaths.Count;
                            PatchSetIndex           = 0;
                            PatchSetCount           = 1;
                            CurrentMetaInstallState = IndexedZiPatchInstaller.InstallTaskState.NotStarted;

                            for (var attemptIndex = 0; attemptIndex < REATTEMPT_COUNT; attemptIndex++)
                            {
                                //repaired=true;
                                //continue;
                                CurrentMetaInstallState = IndexedZiPatchInstaller.InstallTaskState.NotStarted;
                                Progress                = Total = TaskIndex = 0;
                                _reportedProgresses.Clear();

                                await sdoFileInstaller.VerifyFiles
                                    (_settings.GamePath.FullName, attemptIndex > 0, Math.Min(Math.Max(Environment.ProcessorCount - 2, 1), 32), _cancellationTokenSource.Token).ConfigureAwait(false);

                                var brokenFiles = await sdoFileInstaller.GetBrokenFiles().ConfigureAwait(false);
                                _reportedProgresses.Clear();
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
                                        await sdoFileInstaller.QueueInstall(brokenFileIndex, $"\\game\\{filePath}", _cancellationTokenSource.Token).ConfigureAwait(false);
                                    }

                                    CurrentMetaInstallState = IndexedZiPatchInstaller.InstallTaskState.Connecting;
                                    await sdoFileInstaller.Install(MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET, _cancellationTokenSource.Token).ConfigureAwait(false);
                                    CurrentInstallBrokenFileCount = 0;
                                    ResetInstallProgressDisplay();
                                    continue;
                                }

                                CurrentInstallBrokenFileCount = 0;
                                ResetInstallProgressDisplay();
                                break;
                            }

                            if (!repaired)
                                throw new IOException($"Failed to repair after {REATTEMPT_COUNT} attempts");

                            NumBrokenFiles += fileBroken.Count(x => x);
                            PatchSetIndex  =  PatchSetCount;
                        }
                        finally
                        {
                            sdoFileInstaller.OnVerifyProgress  -= UpdateVerifyProgress;
                            sdoFileInstaller.OnInstallProgress -= UpdateInstallProgress;
                        }

                        if (Mode == PatchVerifierMode.Repair)
                        {
                            var gamePath = Path.Combine(_settings.GamePath.FullName, "game");
                            await MoveUnnecessaryFiles(sdoFileInstaller, gamePath, [..targetRelativePaths]).ConfigureAwait(false);
                        }

                        State = VerifyState.Done;
                        break;
                    }

                    case VerifyState.Done:
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException || _cancellationTokenSource.IsCancellationRequested || ex is Win32Exception winex && (uint)winex.HResult == 0x80004005u)
                State = VerifyState.Cancelled;
            else
            {
                Log.Error(ex, "Unexpected error occurred in RunVerifierV3");
                LastException = ex;
                State         = VerifyState.Error;
            }
        }
        finally
        {
            CurrentInstallBrokenFileCount = 0;
            ResetInstallProgressDisplay();
            sdoFileInstaller?.Dispose();
        }
    }

    private void ResetInstallProgressDisplay()
    {
        IsDownloading = false;
        lock (_installProgressLock)
            _currentInstallProgressBySourceIndex.Clear();
    }

    private void UpdateInstallProgressEntry(int sourceIndex, string filePath, long progress, long total)
    {
        IsDownloading = true;
        var effectiveProgress = total > 0 ? Math.Min(progress, total) : progress;
        lock (_installProgressLock)
            _currentInstallProgressBySourceIndex[sourceIndex] = new InstallProgressEntry(filePath, effectiveProgress, total);
    }

    public enum VerifyState
    {
        NotStarted,
        DownloadMeta,
        VerifyAndRepair,
        Done,
        Cancelled,
        Error
    }
}
