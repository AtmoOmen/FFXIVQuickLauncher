#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Common.Patching.SdoFileDownload;

public class SdoFileDownloadLocalInstaller : ISdoFileDownloadInstaller
{
    private bool                      isDisposed;
    private SdoFileDownloadInstaller? instance;
    private string?                   gameRootPath;

    #region Disposal

    public void Dispose()
    {
        if (isDisposed)
            throw new ObjectDisposedException(GetType().FullName);

        instance?.Dispose();
        instance   = null;
        isDisposed = true;
    }

    #endregion

    public Task ConstructFromRemoteIntegrity(IntegrityCheck.IntegrityCheckResult remoteIntegrity, TimeSpan progressReportInterval = default)
    {
        instance?.Dispose();
        instance = new()
        {
            ProgressReportInterval = progressReportInterval.TotalMilliseconds > 0 ? (int)progressReportInterval.TotalMilliseconds : 250
        };

        instance.OnInstallProgress += (index, progress, max,      state) => OnInstallProgress?.Invoke(index, progress, max, state);
        instance.OnVerifyProgress  += (index, count,    progress, max) => OnVerifyProgress?.Invoke(index, count, progress, max);
        instance.ConstructFromRemoteIntegrity(remoteIntegrity);
        return Task.CompletedTask;
    }

    public async Task VerifyFiles(string gameRootPath, bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        this.gameRootPath = gameRootPath;
        await instance.VerifyFiles(gameRootPath, refine, concurrentCount, cancellationToken);
    }

    public Task QueueInstall(int targetIndex, string filePath, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.QueueInstall(targetIndex, filePath);
        return Task.CompletedTask;
    }

    public async Task Install(int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");
        if (string.IsNullOrEmpty(gameRootPath))
            throw new InvalidOperationException("VerifyFiles must run before Install to initialize game root path.");

        await instance.Install(gameRootPath, concurrentCount, cancellationToken);
    }

    public Task<List<string>> GetBrokenFiles(CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        return Task.FromResult(instance.GetBrokenFiles());
    }

    public Task MoveFile(string sourceFile, string targetFile, CancellationToken cancellationToken = default)
    {
        var sourceParentDir = new DirectoryInfo(Path.GetDirectoryName(sourceFile) ?? throw new InvalidOperationException());
        var targetParentDir = new DirectoryInfo
            (Path.GetDirectoryName(targetFile.EndsWith("/", StringComparison.Ordinal) ? targetFile.Substring(0, targetFile.Length - 1) : targetFile) ?? throw new InvalidOperationException());
        targetParentDir.Create();
        Directory.Move(sourceFile, targetFile);

        if (!sourceParentDir.GetFileSystemInfos().Any())
            sourceParentDir.Delete(false);

        return Task.CompletedTask;
    }

    public Task CreateDirectory(string dir, CancellationToken cancellationToken = default)
    {
        new DirectoryInfo(dir).Create();
        return Task.CompletedTask;
    }

    public Task RemoveDirectory(string dir, bool recursive = false, CancellationToken cancellationToken = default)
    {
        new DirectoryInfo(dir).Delete(recursive);
        return Task.CompletedTask;
    }

    public event SdoFileDownloadInstaller.OnInstallProgressDelegate? OnInstallProgress;
    public event SdoFileDownloadInstaller.OnVerifyProgressDelegate?  OnVerifyProgress;
}
