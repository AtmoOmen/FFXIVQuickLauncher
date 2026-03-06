using SharedMemory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game;

#nullable enable

namespace XIVLauncher.Common.Patching.SdoFileDownload;

public class SdoFileDownloadRemoteInstaller : ISdoFileDownloadInstaller
{
    private readonly Process? workerProcess;
    private readonly RpcBuffer subprocessBuffer;
    private int cancellationTokenCounter = 1;
    private long lastProgressUpdateCounter = 0;
    private bool isDisposed = false;

    public event SdoFileDownloadInstaller.OnInstallProgressDelegate? OnInstallProgress;
    public event SdoFileDownloadInstaller.OnVerifyProgressDelegate? OnVerifyProgress;

    public SdoFileDownloadRemoteInstaller(string? workerExecutablePath, bool asAdmin)
    {
        var rpcChannelName = "RemoteSdoFileDownloadInstaller" + Guid.NewGuid();
        this.subprocessBuffer = new(rpcChannelName, RpcResponseHandler);

        if (workerExecutablePath != null)
        {
            this.workerProcess = new();
            this.workerProcess.StartInfo.FileName = workerExecutablePath;
            this.workerProcess.StartInfo.UseShellExecute = true;
            this.workerProcess.StartInfo.Verb = asAdmin ? "runas" : "open";
            this.workerProcess.StartInfo.Arguments = $"sdo-rpc {Process.GetCurrentProcess().Id} {rpcChannelName}";
#if !DEBUG
            this.workerProcess.StartInfo.CreateNoWindow = true;
            this.workerProcess.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
#endif
            this.workerProcess.Start();
        }
        else
        {
            this.workerProcess = null;
            Task.Run(() => new WorkerSubprocessBody(Process.GetCurrentProcess().Id, rpcChannelName).RunToDisposeSelf());
        }
    }

    public void Dispose()
    {
        if (this.isDisposed)
            throw new ObjectDisposedException(GetType().FullName);

        try
        {
            this.subprocessBuffer.RemoteRequest(((MemoryStream)GetRequestCreator(WorkerInboundOpcode.DisposeAndExit).BaseStream).ToArray(), 100);
        }
        catch (Exception)
        {
            // ignore any exception
        }

        if (this.workerProcess != null && !this.workerProcess.HasExited)
        {
            this.workerProcess.WaitForExit(1000);

            try
            {
                this.workerProcess.Kill();
            }
            catch (Exception)
            {
                if (!this.workerProcess.HasExited)
                    throw;
            }
        }

        this.subprocessBuffer.Dispose();
        this.isDisposed = true;
    }

    public async Task ConstructFromRemoteIntegrity(IntegrityCheck.IntegrityCheckResult remoteIntegrity, TimeSpan progressReportInterval = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.Construct);
        WriteIntegrityCheckResult(writer, remoteIntegrity);
        writer.Write(progressReportInterval.TotalMilliseconds > 0 ? (int)progressReportInterval.TotalMilliseconds : 250);
        await WaitForResult(writer);
    }

    public async Task VerifyFiles(string gameRootPath, bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.VerifyFiles, cancellationToken);
        writer.Write(gameRootPath);
        writer.Write(refine);
        writer.Write(concurrentCount);
        await WaitForResult(writer, cancellationToken, 864000000);
    }

    public async Task QueueInstall(int targetIndex, string filePath, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.QueueInstall, cancellationToken);
        writer.Write(targetIndex);
        writer.Write(filePath);
        await WaitForResult(writer, cancellationToken);
    }

    public async Task Install(int concurrentCount = 8, CancellationToken cancellationToken = default)
    {
        var writer = GetRequestCreator(WorkerInboundOpcode.Install, cancellationToken);
        writer.Write(concurrentCount);
        await WaitForResult(writer, cancellationToken, 864000000);
    }

    public async Task<List<string>> GetBrokenFiles(CancellationToken cancellationToken = default)
    {
        using var reader = await WaitForResult(GetRequestCreator(WorkerInboundOpcode.GetBrokenFiles, cancellationToken), cancellationToken, 30000, false);
        return ReadStringList(reader);
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
        var type = (WorkerOutboundOpcode)reader.ReadInt32();

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
        if (progressUpdateCounter < this.lastProgressUpdateCounter)
            return;

        this.lastProgressUpdateCounter = progressUpdateCounter;
        var index = reader.ReadInt32();
        var progress = reader.ReadInt64();
        var max = reader.ReadInt64();
        var state = (SdoFileDownloadInstaller.InstallTaskState)reader.ReadInt32();

        OnInstallProgress?.Invoke(index, progress, max, state);
    }

    private void OnReceiveVerifyProgressUpdate(BinaryReader reader)
    {
        var progressUpdateCounter = reader.ReadInt64();
        if (progressUpdateCounter < this.lastProgressUpdateCounter)
            return;

        this.lastProgressUpdateCounter = progressUpdateCounter;
        var index = reader.ReadInt32();
        var count = reader.ReadInt32();
        var progress = reader.ReadInt64();
        var max = reader.ReadInt64();

        OnVerifyProgress?.Invoke(index, count, progress, max);
    }

    private BinaryWriter GetRequestCreator(WorkerInboundOpcode opcode, CancellationToken cancellationToken = default)
    {
        var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        var tokenId = -1;

        if (cancellationToken.CanBeCanceled)
        {
            tokenId = this.cancellationTokenCounter++;
            cancellationToken.Register(() => _ = this.CancelRemoteTask(tokenId));
        }

        writer.Write(tokenId);
        writer.Write((int)opcode);
        return writer;
    }

    private async Task<BinaryReader> WaitForResult(BinaryWriter req, CancellationToken cancellationToken = default, int timeoutMs = 30000, bool autoDispose = true)
    {
        var requestData = ((MemoryStream)req.BaseStream).ToArray();
        var response = await this.subprocessBuffer.RemoteRequestAsync(requestData, timeoutMs, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (this.isDisposed)
            throw new OperationCanceledException();

        var reader = new BinaryReader(new MemoryStream(response.Data));

        try
        {
            var result = (WorkerResultCode)reader.ReadInt32();
            return result switch
            {
                WorkerResultCode.Pass => reader,
                WorkerResultCode.Cancelled => throw new TaskCanceledException(),
                WorkerResultCode.Error => throw new(reader.ReadString()),
                _ => throw new InvalidOperationException("Invalid WorkerResultCodes"),
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
        if (this.isDisposed)
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

    private static void WriteIntegrityCheckResult(BinaryWriter writer, IntegrityCheck.IntegrityCheckResult remoteIntegrity)
    {
        writer.Write(JsonSerializer.Serialize(remoteIntegrity));
    }

    private static IntegrityCheck.IntegrityCheckResult ReadIntegrityCheckResult(BinaryReader reader)
    {
        return JsonSerializer.Deserialize<IntegrityCheck.IntegrityCheckResult>(reader.ReadString())
               ?? throw new InvalidDataException("Failed to deserialize integrity check result.");
    }

    private static List<string> ReadStringList(BinaryReader reader)
    {
        List<string> result = new();

        for (int i = 0, count = reader.ReadInt32(); i < count; i++)
            result.Add(reader.ReadString());

        return result;
    }

    private static void WriteStringList(BinaryWriter writer, List<string> values)
    {
        writer.Write(values.Count);

        foreach (var value in values)
            writer.Write(value);
    }

    public class WorkerSubprocessBody : IDisposable
    {
        private readonly object progressUpdateSync = new();
        private readonly Process parentProcess;
        private readonly RpcBuffer subprocessBuffer;
        private readonly Dictionary<int, CancellationTokenSource> cancellationTokenSources = new();
        private SdoFileDownloadLocalInstaller? instance = null;
        private long progressUpdateCounter = 0;

        public WorkerSubprocessBody(int monitorProcessId, string channelName)
        {
            this.parentProcess = Process.GetProcessById(monitorProcessId);
            this.subprocessBuffer = new(channelName, async (_, data) =>
            {
                using var reader = new BinaryReader(new MemoryStream(data));
                var cancelSourceId = reader.ReadInt32();
                var cancelToken = default(CancellationToken);

                if (cancelSourceId != -1)
                {
                    lock (this.cancellationTokenSources)
                    {
                        this.cancellationTokenSources[cancelSourceId] = new();
                        cancelToken = this.cancellationTokenSources[cancelSourceId].Token;
                    }
                }

                var method = (WorkerInboundOpcode)reader.ReadInt32();

                var ms = new MemoryStream();
                var writer = new BinaryWriter(ms);
                writer.Write(0);

                try
                {
                    switch (method)
                    {
                        case WorkerInboundOpcode.CancelTask:
                            lock (this.cancellationTokenSources)
                            {
                                if (this.cancellationTokenSources.TryGetValue(reader.ReadInt32(), out var cts))
                                    cts.Cancel();
                            }

                            break;

                        case WorkerInboundOpcode.Construct:
                            this.instance?.Dispose();
                            this.instance = new();
                            this.instance.OnInstallProgress += OnInstallProgressUpdate;
                            this.instance.OnVerifyProgress += OnVerifyProgressUpdate;
                            await this.instance.ConstructFromRemoteIntegrity(ReadIntegrityCheckResult(reader), TimeSpan.FromMilliseconds(reader.ReadInt32()));
                            break;

                        case WorkerInboundOpcode.DisposeAndExit:
                            this.instance?.Dispose();
                            this.instance = null;
                            Environment.Exit(0);
                            break;

                        case WorkerInboundOpcode.VerifyFiles:
                            if (this.instance is null)
                                throw new InvalidOperationException("Installer is not initialized.");

                            await this.instance.VerifyFiles(reader.ReadString(), reader.ReadBoolean(), reader.ReadInt32(), cancelToken);
                            break;

                        case WorkerInboundOpcode.QueueInstall:
                            if (this.instance is null)
                                throw new InvalidOperationException("Installer is not initialized.");

                            await this.instance.QueueInstall(reader.ReadInt32(), reader.ReadString(), cancelToken);
                            break;

                        case WorkerInboundOpcode.Install:
                            if (this.instance is null)
                                throw new InvalidOperationException("Installer is not initialized.");

                            await this.instance.Install(reader.ReadInt32(), cancelToken);
                            break;

                        case WorkerInboundOpcode.GetBrokenFiles:
                            if (this.instance is null)
                                throw new InvalidOperationException("Installer is not initialized.");

                            WriteStringList(writer, await this.instance.GetBrokenFiles(cancelToken));
                            break;

                        case WorkerInboundOpcode.MoveFile:
                        {
                            var sourceFileName = reader.ReadString();
                            var targetFileName = reader.ReadString();

                            var sourceParentDir = new DirectoryInfo(Path.GetDirectoryName(sourceFileName) ?? throw new InvalidOperationException());
                            var targetParentDir =
                                new DirectoryInfo(Path.GetDirectoryName(
                                                      targetFileName.EndsWith("/", StringComparison.Ordinal) ? targetFileName.Substring(0, targetFileName.Length - 1) : targetFileName)
                                                  ?? throw new InvalidOperationException());

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
                            var dir = new DirectoryInfo(reader.ReadString());
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
                    {
                        lock (this.cancellationTokenSources)
                            this.cancellationTokenSources.Remove(cancelSourceId);
                    }
                }

                return ms.ToArray();
            });
        }

        private void OnInstallProgressUpdate(int index, long progress, long max, SdoFileDownloadInstaller.InstallTaskState state)
        {
            lock (this.progressUpdateSync)
            {
                var ms = new MemoryStream();
                var writer = new BinaryWriter(ms);
                writer.Write((int)WorkerOutboundOpcode.UpdateInstallProgress);
                writer.Write(this.progressUpdateCounter);
                writer.Write(index);
                writer.Write(progress);
                writer.Write(max);
                writer.Write((int)state);
                this.progressUpdateCounter += 1;
                this.subprocessBuffer.RemoteRequest(ms.ToArray());
            }
        }

        private void OnVerifyProgressUpdate(int index, int count,long progress, long max)
        {
            lock (this.progressUpdateSync)
            {
                var ms = new MemoryStream();
                var writer = new BinaryWriter(ms);
                writer.Write((int)WorkerOutboundOpcode.UpdateVerifyProgress);
                writer.Write(this.progressUpdateCounter);
                writer.Write(index);
                writer.Write(count);
                writer.Write(progress);
                writer.Write(max);
                this.progressUpdateCounter += 1;
                this.subprocessBuffer.RemoteRequest(ms.ToArray());
            }
        }

        public void Dispose()
        {
            this.subprocessBuffer.Dispose();
            this.instance?.Dispose();
        }

        public void Run()
        {
            this.parentProcess.WaitForExit();
        }

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
    }

    private enum WorkerResultCode
    {
        Pass,
        Cancelled,
        Error,
    }

    private enum WorkerOutboundOpcode
    {
        UpdateInstallProgress,
        UpdateVerifyProgress,
    }

    private enum WorkerInboundOpcode
    {
        CancelTask,
        Construct,
        DisposeAndExit,
        VerifyFiles,
        QueueInstall,
        Install,
        GetBrokenFiles,
        MoveFile,
        CreateDirectory,
        RemoveDirectory,
    }
}
