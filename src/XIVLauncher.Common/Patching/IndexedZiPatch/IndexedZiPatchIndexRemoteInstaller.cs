using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharedMemory;

namespace XIVLauncher.Common.Patching.IndexedZiPatch;

public class IndexedZiPatchIndexRemoteInstaller : IIndexedZiPatchIndexInstaller
{
    private readonly Process?  workerProcess;
    private readonly RpcBuffer subprocessBuffer;
    private          int       cancellationTokenCounter = 1;
    private          long      lastProgressUpdateCounter;
    private          bool      isDisposed;

    public IndexedZiPatchIndexRemoteInstaller(string? workerExecutablePath, bool asAdmin)
    {
        var rpcChannelName = "RemoteZiPatchIndexInstaller" + Guid.NewGuid();
        subprocessBuffer = new(rpcChannelName, RpcResponseHandler);

        if (workerExecutablePath != null)
        {
            workerProcess                           = new();
            workerProcess.StartInfo.FileName        = workerExecutablePath;
            workerProcess.StartInfo.UseShellExecute = true;
            workerProcess.StartInfo.Verb            = asAdmin ? "runas" : "open";
            workerProcess.StartInfo.Arguments       = $"index-rpc {Process.GetCurrentProcess().Id} {rpcChannelName}";
#if !DEBUG
            this.workerProcess.StartInfo.CreateNoWindow = true;
            this.workerProcess.StartInfo.WindowStyle    = ProcessWindowStyle.Hidden;
#endif
            workerProcess.Start();
        }
        else
        {
            workerProcess = null;
            Task.Run(() => new WorkerSubprocessBody(Process.GetCurrentProcess().Id, rpcChannelName).RunToDisposeSelf());
        }
    }

    #region Disposal

    public void Dispose()
    {
        if (isDisposed)
            throw new ObjectDisposedException(GetType().FullName);

        try
        {
            subprocessBuffer.RemoteRequest(((MemoryStream)GetRequestCreator(WorkerInboundOpcode.DisposeAndExit).BaseStream).ToArray(), 100);
        }
        catch (Exception)
        {
            // ignore any exception
        }

        if (workerProcess != null && !workerProcess.HasExited)
        {
            workerProcess.WaitForExit(1000);

            try
            {
                workerProcess.Kill();
            }
            catch (Exception)
            {
                if (!workerProcess.HasExited)
                    throw;
            }
        }

        subprocessBuffer.Dispose();
        isDisposed = true;
    }

    #endregion

    public async Task ConstructFromPatchFile(IndexedZiPatchIndex patchIndex, TimeSpan progressReportInterval = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.Construct);
        patchIndex.WriteTo(writer);
        writer.Write(progressReportInterval.TotalMilliseconds > 0 ? (int)progressReportInterval.TotalMilliseconds : 250);
        await WaitForResult(writer);
    }

    public async Task VerifyFiles(bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.VerifyFiles, cancellationToken);
        writer.Write(refine);
        writer.Write(concurrentCount);
        await WaitForResult(writer, cancellationToken, 864000000);
    }

    public async Task MarkFileAsMissing(int targetIndex, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.MarkFileAsMissing, cancellationToken);
        writer.Write(targetIndex);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task SetTargetStreamFromPathReadOnly(int targetIndex, string path, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.SetTargetStreamFromPathReadOnly, cancellationToken);
        writer.Write(targetIndex);
        writer.Write(path);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task SetTargetStreamFromPathReadWrite(int targetIndex, string path, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.SetTargetStreamFromPathReadWrite, cancellationToken);
        writer.Write(targetIndex);
        writer.Write(path);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task SetTargetStreamsFromPathReadOnly(string rootPath, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.SetTargetStreamsFromPathReadOnly, cancellationToken);
        writer.Write(rootPath);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task SetTargetStreamsFromPathReadWriteForMissingFiles(string rootPath, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.SetTargetStreamsFromPathReadWriteForMissingFiles, cancellationToken);
        writer.Write(rootPath);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task RepairNonPatchData(CancellationToken cancellationToken = default) =>
        await WaitForResult(GetRequestCreator(WorkerInboundOpcode.RepairNonPatchData, cancellationToken), cancellationToken);

    public async Task WriteVersionFiles(string rootPath, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.WriteVersionFiles, cancellationToken);
        writer.Write(rootPath);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task QueueInstall(int sourceIndex, Uri sourceUrl, string? sid, int splitBy = 8, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.QueueInstallFromUrl, cancellationToken);
        writer.Write(sourceIndex);
        writer.Write(sourceUrl.OriginalString);
        writer.Write(sid ?? "");
        writer.Write(splitBy);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task QueueInstall(int sourceIndex, FileInfo sourceFile, int splitBy = 8, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.QueueInstallFromLocalFile, cancellationToken);
        writer.Write(sourceIndex);
        writer.Write(sourceFile.FullName);
        writer.Write(splitBy);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task Install(int concurrentCount, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.Install, cancellationToken);
        writer.Write(concurrentCount);
        await WaitForResult(writer, cancellationToken, 864000000);
    }

    public async Task<List<SortedSet<Tuple<int, int>>>> GetMissingPartIndicesPerPatch(CancellationToken cancellationToken = default)
    {
        using var                        reader = await WaitForResult(GetRequestCreator(WorkerInboundOpcode.GetMissingPartIndicesPerPatch, cancellationToken), cancellationToken, 30000, false);
        List<SortedSet<Tuple<int, int>>> result = new();

        for (int i = 0, iReadLength = reader.ReadInt32(); i < iReadLength; i++)
        {
            SortedSet<Tuple<int, int>> e1 = new();
            for (int j = 0, jReadLength = reader.ReadInt32(); j < jReadLength; j++)
                e1.Add(Tuple.Create(reader.ReadInt32(), reader.ReadInt32()));
            result.Add(e1);
        }

        return result;
    }

    public async Task<List<SortedSet<int>>> GetMissingPartIndicesPerTargetFile(CancellationToken cancellationToken = default)
    {
        using var            reader = await WaitForResult(GetRequestCreator(WorkerInboundOpcode.GetMissingPartIndicesPerTargetFile, cancellationToken), cancellationToken, 30000, false);
        List<SortedSet<int>> result = new();

        for (int i = 0, iReadLength = reader.ReadInt32(); i < iReadLength; i++)
        {
            SortedSet<int> e1 = new();
            for (int j = 0, jReadLength = reader.ReadInt32(); j < jReadLength; j++)
                e1.Add(reader.ReadInt32());
            result.Add(e1);
        }

        return result;
    }

    public async Task<SortedSet<int>> GetSizeMismatchTargetFileIndices(CancellationToken cancellationToken = default)
    {
        using var      reader = await WaitForResult(GetRequestCreator(WorkerInboundOpcode.GetSizeMismatchTargetFileIndices, cancellationToken), cancellationToken, 30000, false);
        SortedSet<int> result = new();
        for (int i = 0, readIndex = reader.ReadInt32(); i < readIndex; i++)
            result.Add(reader.ReadInt32());
        return result;
    }

    public async Task SetWorkerProcessPriority(ProcessPriorityClass subprocessPriority, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.SetWorkerProcessPriority, cancellationToken);
        writer.Write((int)subprocessPriority);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task MoveFile(string sourceFile, string targetFile, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.MoveFile, cancellationToken);
        writer.Write(sourceFile);
        writer.Write(targetFile);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task CreateDirectory(string dir, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.CreateDirectory, cancellationToken);
        writer.Write(dir);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task RemoveDirectory(string dir, bool recursive = false, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.RemoveDirectory, cancellationToken);
        writer.Write(dir);
        writer.Write(recursive);
        await WaitForResult(writer, cancellationToken);
    }

    private void RpcResponseHandler(ulong _, byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        var       type   = (WorkerOutboundOpcode)reader.ReadInt32();

        switch (type)
        {
            case WorkerOutboundOpcode.UpdateInstallProgress:
                OnReceiveInstallProgressUpdate(reader);
                break;

            case WorkerOutboundOpcode.UpdateVerifyProgress:
                OnReceiveVerifyProgressUpdate(reader);
                break;

            default:
                throw new ArgumentException("Unknown recv opc");
        }
    }

    private void OnReceiveInstallProgressUpdate(BinaryReader reader)
    {
        var progressUpdateCounter = reader.ReadInt64();
        if (progressUpdateCounter < lastProgressUpdateCounter)
            return;

        lastProgressUpdateCounter = progressUpdateCounter;
        var index    = reader.ReadInt32();
        var progress = reader.ReadInt64();
        var max      = reader.ReadInt64();
        var state    = (IndexedZiPatchInstaller.InstallTaskState)reader.ReadInt32();

        OnInstallProgress?.Invoke(index, progress, max, state);
    }

    private void OnReceiveVerifyProgressUpdate(BinaryReader reader)
    {
        var progressUpdateCounter = reader.ReadInt64();
        if (progressUpdateCounter < lastProgressUpdateCounter)
            return;

        lastProgressUpdateCounter = progressUpdateCounter;
        var index    = reader.ReadInt32();
        var progress = reader.ReadInt64();
        var max      = reader.ReadInt64();

        OnVerifyProgress?.Invoke(index, progress, max);
    }

    private BinaryWriter GetRequestCreator(WorkerInboundOpcode opcode, CancellationToken cancellationToken = default)
    {
        var ms      = new MemoryStream();
        var writer  = new BinaryWriter(ms);
        var tokenId = -1;

        if (cancellationToken.CanBeCanceled)
        {
            tokenId = cancellationTokenCounter++;
            cancellationToken.Register(() => _ = CancelRemoteTask(tokenId));
        }

        writer.Write(tokenId);
        writer.Write((int)opcode);
        return writer;
    }

    private async Task<BinaryReader> WaitForResult(BinaryWriter req, CancellationToken cancellationToken = default, int timeoutMs = 30000, bool autoDispose = true)
    {
        var requestData = ((MemoryStream)req.BaseStream).ToArray();
        var response    = await subprocessBuffer.RemoteRequestAsync(requestData, timeoutMs, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (isDisposed)
            throw new OperationCanceledException();

        var logPath = Path.Combine(XIVLauncher.Common.Constant.Paths.RoamingPath, "patcher.log");
        if (!response.Success)
        {
            if (workerProcess is { HasExited: true } exitedWorkerProcess)
                throw new IOException($"远端补丁索引进程在响应前退出，退出码 {exitedWorkerProcess.ExitCode}，可查看日志: {logPath}");

            throw new TimeoutException($"远端补丁索引进程未在 {timeoutMs} ms 内返回有效响应，可查看日志: {logPath}");
        }

        if (response.Data is null || response.Data.Length < sizeof(int))
        {
            if (workerProcess is { HasExited: true } exitedWorkerProcess)
                throw new IOException($"远端补丁索引进程返回空响应后退出，退出码 {exitedWorkerProcess.ExitCode}，可查看日志: {logPath}");

            throw new IOException($"远端补丁索引进程返回了空响应，可查看日志: {logPath}");
        }

        var reader = new BinaryReader(new MemoryStream(response.Data));

        try
        {
            var result = (WorkerResultCode)reader.ReadInt32();
            return result switch
            {
                WorkerResultCode.Pass      => reader,
                WorkerResultCode.Cancelled => throw new TaskCanceledException(),
                WorkerResultCode.Error     => throw new(reader.ReadString()),
                _                          => throw new InvalidOperationException("Invalid WorkerResultCodes")
            };
        }
        finally
        {
            if (autoDispose)
                reader.Dispose();
        }
    }

    private async Task CancelRemoteTask(int tokenId)
    {
        if (isDisposed)
            return;

        try
        {
            var writer = GetRequestCreator(WorkerInboundOpcode.CancelTask);
            writer.Write(tokenId);
            await WaitForResult(writer);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    public event IndexedZiPatchInstaller.OnInstallProgressDelegate? OnInstallProgress;
    public event IndexedZiPatchInstaller.OnVerifyProgressDelegate?  OnVerifyProgress;

    public class WorkerSubprocessBody : IDisposable
    {
        private readonly object                                   progressUpdateSync = new();
        private readonly Process                                  parentProcess;
        private readonly RpcBuffer                                subprocessBuffer;
        private readonly Dictionary<int, CancellationTokenSource> cancellationTokenSources = new();
        private          IndexedZiPatchInstaller?                 instance;
        private          long                                     progressUpdateCounter;

        public WorkerSubprocessBody(int monitorProcessId, string channelName)
        {
            parentProcess = Process.GetProcessById(monitorProcessId);
            subprocessBuffer = new
            (
                channelName,
                async (_, data) =>
                {
                    using var reader         = new BinaryReader(new MemoryStream(data));
                    var       cancelSourceId = reader.ReadInt32();
                    var       cancelToken    = default(CancellationToken);

                    if (cancelSourceId != -1)
                    {
                        cancellationTokenSources[cancelSourceId] = new();
                        cancelToken                              = cancellationTokenSources[cancelSourceId].Token;
                    }

                    var method = (WorkerInboundOpcode)reader.ReadInt32();

                    var ms     = new MemoryStream();
                    var writer = new BinaryWriter(ms);
                    writer.Write(0);

                    try
                    {
                        switch (method)
                        {
                            case WorkerInboundOpcode.CancelTask:
                                lock (cancellationTokenSources)
                                {
                                    if (cancellationTokenSources.TryGetValue(reader.ReadInt32(), out var cts))
                                        cts.Cancel();
                                }

                                break;

                            case WorkerInboundOpcode.Construct:
                                instance?.Dispose();
                                instance = new(new(reader, false))
                                {
                                    ProgressReportInterval = reader.ReadInt32()
                                };
                                instance.OnInstallProgress += OnInstallProgressUpdate;
                                instance.OnVerifyProgress  += OnVerifyProgressUpdate;
                                break;

                            case WorkerInboundOpcode.DisposeAndExit:
                                instance?.Dispose();
                                instance = null;
                                Environment.Exit(0);
                                break;

                            case WorkerInboundOpcode.VerifyFiles:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                await instance.VerifyFiles(reader.ReadBoolean(), reader.ReadInt32(), cancelToken);
                                break;

                            case WorkerInboundOpcode.MarkFileAsMissing:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.MarkFileAsMissing(reader.ReadInt32());
                                break;

                            case WorkerInboundOpcode.SetTargetStreamFromPathReadOnly:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.SetTargetStreamForRead(reader.ReadInt32(), new FileStream(reader.ReadString(), FileMode.Open, FileAccess.Read));
                                break;

                            case WorkerInboundOpcode.SetTargetStreamFromPathReadWrite:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.SetTargetStreamForWriteFromFile(reader.ReadInt32(), new(reader.ReadString()));
                                break;

                            case WorkerInboundOpcode.SetTargetStreamsFromPathReadOnly:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.SetTargetStreamsFromPathReadOnly(reader.ReadString());
                                break;

                            case WorkerInboundOpcode.SetTargetStreamsFromPathReadWriteForMissingFiles:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.SetTargetStreamsFromPathReadWriteForMissingFiles(reader.ReadString());
                                break;

                            case WorkerInboundOpcode.RepairNonPatchData:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                await instance.RepairNonPatchData(cancelToken);
                                break;

                            case WorkerInboundOpcode.WriteVersionFiles:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.WriteVersionFiles(reader.ReadString());
                                break;

                            case WorkerInboundOpcode.QueueInstallFromUrl:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.QueueInstall(reader.ReadInt32(), reader.ReadString(), reader.ReadString(), reader.ReadInt32());
                                break;

                            case WorkerInboundOpcode.QueueInstallFromLocalFile:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                instance.QueueInstall(reader.ReadInt32(), new(reader.ReadString()), reader.ReadInt32());
                                break;

                            case WorkerInboundOpcode.Install:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                await instance.Install(reader.ReadInt32(), cancelToken);
                                break;

                            case WorkerInboundOpcode.GetMissingPartIndicesPerPatch:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                writer.Write(instance.MissingPartIndicesPerPatch.Count);

                                foreach (var e1 in instance.MissingPartIndicesPerPatch)
                                {
                                    writer.Write(e1.Count);

                                    foreach (var e2 in e1)
                                    {
                                        writer.Write(e2.Item1);
                                        writer.Write(e2.Item2);
                                    }
                                }

                                break;

                            case WorkerInboundOpcode.GetMissingPartIndicesPerTargetFile:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                writer.Write(instance.MissingPartIndicesPerTargetFile.Count);

                                foreach (var e1 in instance.MissingPartIndicesPerTargetFile)
                                {
                                    writer.Write(e1.Count);
                                    foreach (var e2 in e1)
                                        writer.Write(e2);
                                }

                                break;

                            case WorkerInboundOpcode.GetSizeMismatchTargetFileIndices:
                                if (instance is null)
                                    throw new InvalidOperationException("Installer is not initialized.");

                                writer.Write(instance.SizeMismatchTargetFileIndices.Count);
                                foreach (var e1 in instance.SizeMismatchTargetFileIndices)
                                    writer.Write(e1);
                                break;

                            case WorkerInboundOpcode.SetWorkerProcessPriority:
                                Process.GetCurrentProcess().PriorityClass = (ProcessPriorityClass)reader.ReadInt32();
                                break;

                            case WorkerInboundOpcode.MoveFile:
                            {
                                var sourceFileName = reader.ReadString();
                                var targetFileName = reader.ReadString();

                                var sourceParentDir = new DirectoryInfo(Path.GetDirectoryName(sourceFileName) ?? throw new InvalidOperationException());
                                var targetParentDir =
                                    new DirectoryInfo
                                    (
                                        Path.GetDirectoryName
                                        (
                                            targetFileName.EndsWith("/", StringComparison.Ordinal) ? targetFileName.Substring(0, targetFileName.Length - 1) : targetFileName
                                        )
                                        ?? throw new InvalidOperationException()
                                    );

                                targetParentDir.Create();
                                Directory.Move(sourceFileName, targetFileName);

                                if (!sourceParentDir.GetFileSystemInfos().Any())
                                    sourceParentDir.Delete(false);
                                break;
                            }

                            case WorkerInboundOpcode.CreateDirectory:
                                new DirectoryInfo(reader.ReadString()).Create();
                                break;

                            case WorkerInboundOpcode.RemoveDirectory:
                            {
                                var dir       = new DirectoryInfo(reader.ReadString());
                                var recursive = reader.ReadBoolean();
                                dir.Delete(recursive);
                                break;
                            }

                            default:
                                throw new InvalidOperationException("Invalid WorkerInboundOpcode");
                        }

                        writer.Seek(0, SeekOrigin.Begin);
                        writer.Write((int)WorkerResultCode.Pass);
                    }
                    catch (Exception ex)
                    {
                        writer.Seek(0, SeekOrigin.Begin);

                        if (ex is OperationCanceledException)
                            writer.Write((int)WorkerResultCode.Cancelled);
                        else
                        {
                            writer.Write((int)WorkerResultCode.Error);
                            writer.Write(ex.ToString());
                        }
                    }
                    finally
                    {
                        if (cancelSourceId != -1)
                            cancellationTokenSources.Remove(cancelSourceId);
                    }

                    return ms.ToArray();
                }
            );
        }

        #region Disposal

        public void Dispose()
        {
            subprocessBuffer.Dispose();
            instance?.Dispose();
        }

        #endregion

        public void Run() =>
            parentProcess.WaitForExit();

        public void RunToDisposeSelf()
        {
            try
            {
                Run();
            }
            catch (OperationCanceledException)
            {
                // pass
            }
            finally
            {
                Dispose();
            }
        }

        private void OnInstallProgressUpdate(int index, long progress, long max, IndexedZiPatchInstaller.InstallTaskState state)
        {
            lock (progressUpdateSync)
            {
                var ms     = new MemoryStream();
                var writer = new BinaryWriter(ms);
                writer.Write((int)WorkerOutboundOpcode.UpdateInstallProgress);
                writer.Write(progressUpdateCounter);
                writer.Write(index);
                writer.Write(progress);
                writer.Write(max);
                writer.Write((int)state);
                progressUpdateCounter += 1;
                subprocessBuffer.RemoteRequest(ms.ToArray());
            }
        }

        private void OnVerifyProgressUpdate(int index, long progress, long max)
        {
            lock (progressUpdateSync)
            {
                var ms     = new MemoryStream();
                var writer = new BinaryWriter(ms);
                writer.Write((int)WorkerOutboundOpcode.UpdateVerifyProgress);
                writer.Write(progressUpdateCounter);
                writer.Write(index);
                writer.Write(progress);
                writer.Write(max);
                progressUpdateCounter += 1;
                subprocessBuffer.RemoteRequest(ms.ToArray());
            }
        }
    }

    private enum WorkerResultCode
    {
        Pass,
        Cancelled,
        Error
    }

    private enum WorkerOutboundOpcode
    {
        UpdateInstallProgress,
        UpdateVerifyProgress
    }

    private enum WorkerInboundOpcode
    {
        CancelTask,
        Construct,
        DisposeAndExit,
        VerifyFiles,
        MarkFileAsMissing,
        SetTargetStreamFromPathReadOnly,
        SetTargetStreamFromPathReadWrite,
        SetTargetStreamsFromPathReadOnly,
        SetTargetStreamsFromPathReadWriteForMissingFiles,
        RepairNonPatchData,
        WriteVersionFiles,
        QueueInstallFromUrl,
        QueueInstallFromLocalFile,
        Install,
        GetMissingPartIndicesPerPatch,
        GetMissingPartIndicesPerTargetFile,
        GetSizeMismatchTargetFileIndices,
        SetWorkerProcessPriority,
        MoveFile,
        CreateDirectory,
        RemoveDirectory
    }
}
