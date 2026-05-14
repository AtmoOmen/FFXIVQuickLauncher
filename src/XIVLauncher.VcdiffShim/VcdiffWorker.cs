using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;
using SharedMemory;

namespace XIVLauncher.VcdiffShim;

public class VcdiffWorker : IDisposable
{
    private readonly Process   parentProcess;
    private readonly RpcBuffer rpcBuffer;
    private          bool      isDisposed;

    public VcdiffWorker(int monitorProcessId, string channelName)
    {
        parentProcess = Process.GetProcessById(monitorProcessId);
        rpcBuffer = new(channelName, HandleRequestAsync);
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        rpcBuffer.Dispose();
        isDisposed = true;
    }

    public void Run()
    {
        try
        {
            parentProcess.WaitForExit();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Dispose();
        }
    }

    private async Task<byte[]> HandleRequestAsync(ulong _, byte[] data)
    {
        using var reader = new BinaryReader(new MemoryStream(data));
        var       opcode = reader.ReadInt32();

        if (opcode != VCDIFF_OPCODE)
        {
            Log.Error("[VcdiffShim] 未知 opcode: {Opcode}", opcode);
            return BuildErrorResponse("未知的操作码");
        }

        var sourceFile  = reader.ReadString();
        var deltaFile   = reader.ReadString();
        var targetFile  = reader.ReadString();
        var expectedMd5 = reader.ReadString();
        var expectedSize = reader.ReadInt64();

        Log.Information("[VcdiffShim] 收到差分合并请求, 源 {SourceFile}, 差分 {DeltaFile}, 目标 {TargetFile}", sourceFile, deltaFile, targetFile);

        try
        {
            ApplyVcdiffInternal(sourceFile, deltaFile, targetFile, expectedMd5, expectedSize);
            return BuildSuccessResponse();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[VcdiffShim] 差分合并失败");
            return BuildErrorResponse(ex.ToString());
        }
    }

    private void ApplyVcdiffInternal(string sourceFile, string deltaFile, string targetFile, string expectedMd5, long expectedSize)
    {
        if (Environment.Is64BitProcess)
            throw new InvalidOperationException("V3 差分必须在 32 位进程中执行");

        var moduleDirectory = Path.Combine(AppContext.BaseDirectory, "Launcher3Modules");
        var modulePath      = Path.Combine(moduleDirectory, "XDelta3WrapFactory.dll");

        if (!File.Exists(modulePath))
            throw new FileNotFoundException("缺少 V3 差分模块", modulePath);

        Log.Information("[VcdiffShim] 使用 V3 差分模块目录 {ModuleDirectory}", moduleDirectory);

        foreach (var moduleName in new[] { "GlobalSharedEnv.dll", "log4cplusU.dll", "minizip.dll", "XDelta3WrapFactory.dll", "ZlibWrap.dll", "zlib1.dll" })
        {
            var requiredModulePath = Path.Combine(moduleDirectory, moduleName);
            if (!File.Exists(requiredModulePath))
                throw new FileNotFoundException("缺少 V3 差分模块依赖", requiredModulePath);
        }

        var targetParentDir = Path.GetDirectoryName(targetFile) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(targetParentDir);

        var tempPath = string.Concat(targetFile, TEMP_EXTENSION);
        var complete = false;

        try
        {
            Log.Information
            (
                "[VcdiffShim] 差分合并开始, 源 {SourceFile}, 差分 {DeltaFile}, 目标 {TargetFile}, 临时文件 {TempPath}, 期望大小 {ExpectedSize}",
                sourceFile, deltaFile, targetFile, tempPath, expectedSize
            );

            var currentDirectory = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(moduleDirectory);

            IntPtr library = IntPtr.Zero;
            try
            {
                if (!SetDllDirectory(moduleDirectory))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var mergeTicks = Stopwatch.GetTimestamp();
                Log.Information("[VcdiffShim] 正在合并差分 {DeltaPath}, 源 {SourcePath}, 临时目标 {TempPath}", deltaFile, sourceFile, tempPath);
                library = NativeLibrary.Load(modulePath);
                var mergeFile = Marshal.GetDelegateForFunctionPointer<MergeFileDelegate>(NativeLibrary.GetExport(library, "MergeFile"));

                if (!mergeFile(sourceFile, deltaFile, tempPath))
                {
                    var sourceInfo = new FileInfo(sourceFile);
                    Log.Error
                    (
                        "[VcdiffShim] 差分合并失败, 源 {SourcePath} (大小 {SourceSize}), 差分 {DeltaPath}, 临时目标 {TempPath}",
                        sourceFile, sourceInfo.Length, deltaFile, tempPath
                    );
                    throw new InvalidDataException($"V3 差分合并失败: 源文件 {sourceInfo.Length} 字节, 差分 {new FileInfo(deltaFile).Length} 字节");
                }

                Log.Information("[VcdiffShim] 差分合并完成, 耗时 {ElapsedMs} ms", Stopwatch.GetElapsedTime(mergeTicks).TotalMilliseconds);
            }
            finally
            {
                Directory.SetCurrentDirectory(currentDirectory);

                if (library != IntPtr.Zero)
                    NativeLibrary.Free(library);

                SetDllDirectory(null);
            }

            if (expectedSize >= 0 && new FileInfo(tempPath).Length != expectedSize)
                throw new InvalidDataException("V3 差分产物大小不匹配");

            if (!string.IsNullOrWhiteSpace(expectedMd5))
            {
                Log.Information("[VcdiffShim] 正在校验差分合并产物 {TempPath}", tempPath);
                using var stream = File.OpenRead(tempPath);
                var       hash   = MD5.HashData(stream);

                if (!string.Equals(Convert.ToHexString(hash), expectedMd5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("V3 差分产物校验失败");
            }

            File.Move(tempPath, targetFile, true);

            var decodedSize = new FileInfo(targetFile).Length;
            complete = true;
            Log.Information("[VcdiffShim] 差分合并完成 {TargetFile}, 大小 {DecodedSize}", targetFile, decodedSize);
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
    }

    private static byte[] BuildSuccessResponse()
    {
        var ms     = new MemoryStream();
        var writer = new BinaryWriter(ms);
        writer.Write(RESULT_PASS);
        return ms.ToArray();
    }

    private static byte[] BuildErrorResponse(string errorMessage)
    {
        var ms     = new MemoryStream();
        var writer = new BinaryWriter(ms);
        writer.Write(RESULT_ERROR);
        writer.Write(errorMessage);
        return ms.ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? pathName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool MergeFileDelegate(string sourceFile, string deltaFile, string targetFile);

    #region Constants

    private const int    VCDIFF_OPCODE   = 0;
    private const int    RESULT_PASS     = 0;
    private const int    RESULT_ERROR    = 2;
    private const string TEMP_EXTENSION  = ".tmp";

    #endregion
}
