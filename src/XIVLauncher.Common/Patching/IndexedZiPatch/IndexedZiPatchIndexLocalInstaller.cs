#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Patching.IndexedZiPatch;

public class IndexedZiPatchIndexLocalInstaller : IIndexedZiPatchIndexInstaller
{
    private bool                     isDisposed;
    private IndexedZiPatchInstaller? instance;

    #region Disposal

    public void Dispose()
    {
        if (isDisposed)
            throw new ObjectDisposedException(GetType().FullName);

        isDisposed = true;
    }

    #endregion

    public Task ConstructFromPatchFile(IndexedZiPatchIndex patchIndex, TimeSpan progressReportInterval = default)
    {
        instance?.Dispose();
        instance = new(patchIndex)
        {
            ProgressReportInterval = progressReportInterval.TotalMilliseconds > 0 ? (int)progressReportInterval.TotalMilliseconds : 250
        };
        instance.OnInstallProgress += OnInstallProgress;
        instance.OnVerifyProgress  += OnVerifyProgress;
        return Task.CompletedTask;
    }

    public async Task VerifyFiles(bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        await instance.VerifyFiles(refine, concurrentCount, cancellationToken);
    }

    public Task MarkFileAsMissing(int targetIndex, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.MarkFileAsMissing(targetIndex);
        return Task.CompletedTask;
    }

    public Task SetTargetStreamFromPathReadOnly(int targetIndex, string path, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.SetTargetStreamForRead(targetIndex, new FileStream(path, FileMode.Open, FileAccess.Read));
        return Task.CompletedTask;
    }

    public Task SetTargetStreamFromPathReadWrite(int targetIndex, string path, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.SetTargetStreamForWriteFromFile(targetIndex, new(path));
        return Task.CompletedTask;
    }

    public Task SetTargetStreamsFromPathReadOnly(string rootPath, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.SetTargetStreamsFromPathReadOnly(rootPath);
        return Task.CompletedTask;
    }

    public Task SetTargetStreamsFromPathReadWriteForMissingFiles(string rootPath, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.SetTargetStreamsFromPathReadWriteForMissingFiles(rootPath);
        return Task.CompletedTask;
    }

    public async Task RepairNonPatchData(CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        await instance.RepairNonPatchData(cancellationToken);
    }

    public Task WriteVersionFiles(string rootPath, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.WriteVersionFiles(rootPath);
        return Task.CompletedTask;
    }

    public Task QueueInstall(int sourceIndex, Uri sourceUrl, string? sid, int splitBy = 8, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.QueueInstall(sourceIndex, sourceUrl.OriginalString, sid, splitBy);
        return Task.CompletedTask;
    }

    public Task QueueInstall(int sourceIndex, FileInfo sourceFile, int splitBy = 8, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        instance.QueueInstall(sourceIndex, sourceFile, splitBy);
        return Task.CompletedTask;
    }

    public async Task Install(int concurrentCount, CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        await instance.Install(concurrentCount, cancellationToken);
    }

    public Task<List<SortedSet<Tuple<int, int>>>> GetMissingPartIndicesPerPatch(CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        return Task.FromResult(instance.MissingPartIndicesPerPatch);
    }

    public Task<List<SortedSet<int>>> GetMissingPartIndicesPerTargetFile(CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        return Task.FromResult(instance.MissingPartIndicesPerTargetFile);
    }

    public Task<SortedSet<int>> GetSizeMismatchTargetFileIndices(CancellationToken cancellationToken = default)
    {
        if (instance is null)
            throw new InvalidOperationException("Installer is not initialized.");

        return Task.FromResult(instance.SizeMismatchTargetFileIndices);
    }

    public Task SetWorkerProcessPriority(ProcessPriorityClass subprocessPriority, CancellationToken cancellationToken = default) =>
        Task.CompletedTask; // is a no-op locally

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

    public event IndexedZiPatchInstaller.OnInstallProgressDelegate? OnInstallProgress;
    public event IndexedZiPatchInstaller.OnVerifyProgressDelegate?  OnVerifyProgress;
}
