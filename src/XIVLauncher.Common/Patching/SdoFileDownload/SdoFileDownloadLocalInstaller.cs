using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Integrity;

namespace XIVLauncher.Common.Patching.SdoFileDownload;

public class SdoFileDownloadLocalInstaller : ISdoFileDownloadInstaller
{
    private bool                      isDisposed;
    private SdoFileDownloadInstaller? instance;
    private string?                   gameRootPath;

    public void Dispose()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);

        instance?.Dispose();
        instance   = null;
        isDisposed = true;
    }

    public Task ConstructFromRemoteIntegrity(IntegrityCheckResult remoteIntegrity, TimeSpan progressReportInterval = default)
    {
        instance?.Dispose();
        instance = new()
        {
            ProgressReportInterval = progressReportInterval.TotalMilliseconds > 0 ? (int)progressReportInterval.TotalMilliseconds : DEFAULT_PROGRESS_REPORT_INTERVAL
        };

        instance.OnInstallProgress += OnInstanceInstallProgress;
        instance.OnVerifyProgress  += OnInstanceVerifyProgress;
        instance.ConstructFromRemoteIntegrity(remoteIntegrity);
        return Task.CompletedTask;
    }

    public Task VerifyFiles(string gameRootPath, bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        this.gameRootPath = gameRootPath;
        return GetInstance().VerifyFiles(gameRootPath, refine, concurrentCount, cancellationToken);
    }

    public Task QueueInstall(int targetIndex, string filePath, CancellationToken cancellationToken = default)
    {
        GetInstance().QueueInstall(targetIndex, filePath);
        return Task.CompletedTask;
    }

    public Task Install(int concurrentCount = 8, CancellationToken cancellationToken = default) =>
        GetInstance().Install(gameRootPath ?? throw new InvalidOperationException("VerifyFiles must run before Install to initialize game root path."), concurrentCount, cancellationToken);

    public Task<List<string>> GetBrokenFiles(CancellationToken cancellationToken = default) =>
        Task.FromResult(GetInstance().GetBrokenFiles());

    public Task MoveFile(string sourceFile, string targetFile, CancellationToken cancellationToken = default)
    {
        var sourceParentDir = Path.GetDirectoryName(sourceFile)                    ?? throw new InvalidOperationException();
        var targetParentDir = Path.GetDirectoryName(targetFile.TrimEnd('/', '\\')) ?? throw new InvalidOperationException();

        Directory.CreateDirectory(targetParentDir);
        if (File.Exists(sourceFile))
            File.Move(sourceFile, targetFile);
        else
            Directory.Move(sourceFile, targetFile);

        if (Directory.GetFileSystemEntries(sourceParentDir).Length == 0)
            Directory.Delete(sourceParentDir);

        return Task.CompletedTask;
    }

    public Task WriteAllText(string filePath, string content, CancellationToken cancellationToken = default)
    {
        var directoryPath = Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(directoryPath);
        File.WriteAllText(filePath, content, new UTF8Encoding(false));
        return Task.CompletedTask;
    }

    public Task CreateDirectory(string dir, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dir);
        return Task.CompletedTask;
    }

    public Task RemoveDirectory(string dir, bool recursive = false, CancellationToken cancellationToken = default)
    {
        Directory.Delete(dir, recursive);
        return Task.CompletedTask;
    }

    private SdoFileDownloadInstaller GetInstance() =>
        instance ?? throw new InvalidOperationException("Installer is not initialized.");

    private void OnInstanceInstallProgress(int index, long progress, long max, SdoFileDownloadInstaller.InstallTaskState state) =>
        OnInstallProgress?.Invoke(index, progress, max, state);

    private void OnInstanceVerifyProgress(int index, int count, long progress, long max) =>
        OnVerifyProgress?.Invoke(index, count, progress, max);

    public event SdoFileDownloadInstaller.OnInstallProgressDelegate? OnInstallProgress;

    public event SdoFileDownloadInstaller.OnVerifyProgressDelegate? OnVerifyProgress;

    #region Constants

    private const int DEFAULT_PROGRESS_REPORT_INTERVAL = 250;

    #endregion
}
