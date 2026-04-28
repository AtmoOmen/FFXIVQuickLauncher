using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
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

    public Task ApplyVcdiff
    (
        string                                  sourceFile,
        string                                  deltaFile,
        string                                  targetFile,
        string                                  expectedMd5,
        long                                    expectedSize,
        IProgress<(long Progress, long Total)>? progress          = null,
        CancellationToken                       cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targetParentDir = Path.GetDirectoryName(targetFile) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(targetParentDir);

        var tempPath = string.Concat(targetFile, TEMP_EXTENSION);
        var complete = false;

        try
        {
            OnInstallProgress?.Invoke(0, 0, 0, SdoFileDownloadInstaller.InstallTaskState.Downloading);
            Log.Information("[V3Patch] 本地差分合并开始, 源 {SourceFile}, 差分 {DeltaFile}, 目标 {TargetFile}, 临时文件 {TempPath}, 期望大小 {ExpectedSize}, 期望 MD5 {ExpectedMd5}", sourceFile, deltaFile, targetFile, tempPath, expectedSize, expectedMd5);

            if (Environment.Is64BitProcess)
                throw new InvalidOperationException("V3 差分必须在 32 位补丁进程中安装");

            var moduleDirectory = Path.Combine(AppContext.BaseDirectory, "Launcher3Modules");
            var modulePath      = Path.Combine(moduleDirectory,          "XDelta3WrapFactory.dll");
            if (!File.Exists(modulePath))
                throw new FileNotFoundException("缺少 V3 差分模块", modulePath);

            Log.Information("[V3Patch] 使用 V3 差分模块目录 {ModuleDirectory}", moduleDirectory);
            foreach (var moduleName in new[] { "GlobalSharedEnv.dll", "log4cplusU.dll", "minizip.dll", "XDelta3WrapFactory.dll", "ZlibWrap.dll", "zlib1.dll" })
            {
                var requiredModulePath = Path.Combine(moduleDirectory, moduleName);
                if (!File.Exists(requiredModulePath))
                    throw new FileNotFoundException("缺少 V3 差分模块依赖", requiredModulePath);
            }

            var reportTotal = expectedSize >= 0 ? expectedSize : new FileInfo(sourceFile).Length;
            if (reportTotal <= 0)
                reportTotal = 1;

            var       library                         = IntPtr.Zero;
            using var progressCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var       progressCancellationToken       = progressCancellationTokenSource.Token;
            var progressTask = Task.Run
            (
                async () =>
                {
                    var lastSize  = 0L;
                    var lastTicks = Stopwatch.GetTimestamp();

                    while (!progressCancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            var size  = File.Exists(tempPath) ? new FileInfo(tempPath).Length : lastSize;
                            var total = size > 0 ? Math.Max(reportTotal, size) : 0;

                            if (size != lastSize || Stopwatch.GetTimestamp() - lastTicks >= Stopwatch.Frequency)
                            {
                                progress?.Report((size, total));
                                OnInstallProgress?.Invoke(0, size, total, SdoFileDownloadInstaller.InstallTaskState.Downloading);
                                lastSize  = size;
                                lastTicks = Stopwatch.GetTimestamp();
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // ignored
                        }

                        try
                        {
                            await Task.Delay(DEFAULT_PROGRESS_REPORT_INTERVAL, progressCancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // ignored
                        }
                    }
                },
                CancellationToken.None
            );
            {
                try
                {
                    if (!SetDllDirectory(moduleDirectory))
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    var mergeTicks = Stopwatch.GetTimestamp();
                    Log.Information("[V3Patch] 正在合并差分 {DeltaPath}, 目标 {TargetPath}", deltaFile, targetFile);
                    library = NativeLibrary.Load(modulePath);
                    var mergeFile = Marshal.GetDelegateForFunctionPointer<MergeFileDelegate>(NativeLibrary.GetExport(library, "MergeFile"));
                    if (!mergeFile(sourceFile, deltaFile, tempPath))
                        throw new InvalidDataException("V3 差分合并失败");

                    Log.Information("[V3Patch] 差分合并完成 {TargetPath}, 耗时 {ElapsedMs} ms", targetFile, Stopwatch.GetElapsedTime(mergeTicks).TotalMilliseconds);
                }
                finally
                {
                    progressCancellationTokenSource.Cancel();
                    progressTask.GetAwaiter().GetResult();

                    if (library != IntPtr.Zero)
                        NativeLibrary.Free(library);

                    SetDllDirectory(null);
                }
            }

            if (expectedSize >= 0 && new FileInfo(tempPath).Length != expectedSize)
                throw new InvalidDataException("V3 差分产物大小不匹配");

            if (!string.IsNullOrWhiteSpace(expectedMd5))
            {
                Log.Information("[V3Patch] 正在校验差分合并产物 {TempPath}", tempPath);
                using var stream = File.OpenRead(tempPath);
                var       hash   = MD5.HashData(stream);

                if (!string.Equals(Convert.ToHexString(hash), expectedMd5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("V3 差分产物校验失败");
            }

            var decodedSize = new FileInfo(tempPath).Length;
            Log.Information("[V3Patch] 正在替换目标文件 {TargetFile}, 产物大小 {DecodedSize}", targetFile, decodedSize);
            File.Move(tempPath, targetFile, true);
            complete = true;
            OnInstallProgress?.Invoke(0, decodedSize, decodedSize, SdoFileDownloadInstaller.InstallTaskState.Complete);
            Log.Information("[V3Patch] 本地差分合并完成 {TargetFile}", targetFile);
        }
        finally
        {
            if (!complete)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return Task.CompletedTask;
    }

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? pathName);

    public event SdoFileDownloadInstaller.OnInstallProgressDelegate? OnInstallProgress;

    public event SdoFileDownloadInstaller.OnVerifyProgressDelegate? OnVerifyProgress;

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool MergeFileDelegate(string sourceFile, string deltaFile, string targetFile);

    #region Constants

    private const int    DEFAULT_PROGRESS_REPORT_INTERVAL = 250;
    private const string TEMP_EXTENSION                   = ".tmp";

    #endregion
}
