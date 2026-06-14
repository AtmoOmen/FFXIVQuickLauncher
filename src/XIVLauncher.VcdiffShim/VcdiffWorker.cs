using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;
using SharedMemory;

namespace XIVLauncher.VcdiffShim;

public class VcdiffWorker : IDisposable
{
    private readonly Process                                     parentProcess;
    private readonly RpcBuffer                                   rpcBuffer;
    private readonly IntPtr                                      helperLibrary;
    private readonly XdeltaDecodeFileWithDeltaMemoryUtf8Delegate decodeWithDeltaMemory;
    private readonly XdeltaBridgeGetLastErrorUtf8Delegate        getLastError;
    private          bool                                        isDisposed;

    public VcdiffWorker(int monitorProcessId, string channelName)
    {
        parentProcess = Process.GetProcessById(monitorProcessId);
        rpcBuffer     = new(channelName, HandleRequestAsync);

        var helperPath = Path.Combine(AppContext.BaseDirectory, "xivlauncher_xdelta_shim.dll");
        if (!File.Exists(helperPath))
            throw new FileNotFoundException("缺少 V3 差分模块", helperPath);

        try
        {
            helperLibrary = NativeLibrary.Load(helperPath);
            var export = NativeLibrary.GetExport(helperLibrary, "xdelta_decode_file_with_delta_memory_utf8");
            decodeWithDeltaMemory = Marshal.GetDelegateForFunctionPointer<XdeltaDecodeFileWithDeltaMemoryUtf8Delegate>(export);
            var errorExport = NativeLibrary.GetExport(helperLibrary, "xdelta_bridge_get_last_error_utf8");
            getLastError = Marshal.GetDelegateForFunctionPointer<XdeltaBridgeGetLastErrorUtf8Delegate>(errorExport);
        }
        catch
        {
            if (helperLibrary != IntPtr.Zero)
                NativeLibrary.Free(helperLibrary);

            throw;
        }
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        rpcBuffer.Dispose();

        if (helperLibrary != IntPtr.Zero)
            NativeLibrary.Free(helperLibrary);

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

        var sourceFile   = reader.ReadString();
        var targetFile   = reader.ReadString();
        var expectedMd5  = reader.ReadString();
        var expectedSize = reader.ReadInt64();
        var deltaSize    = reader.ReadInt32();

        if (deltaSize < 0)
            throw new InvalidDataException("V3 差分数据长度无效");

        var deltaData = reader.ReadBytes(deltaSize);
        if (deltaData.Length != deltaSize)
            throw new InvalidDataException("V3 差分数据不完整");

        Log.Information("[VcdiffShim] 收到差分合并请求, 源 {SourceFile}, 差分大小 {DeltaSize}, 目标 {TargetFile}", sourceFile, deltaData.Length, targetFile);

        try
        {
            ApplyVcdiffInternal(sourceFile, deltaData, targetFile, expectedMd5, expectedSize);
            return BuildSuccessResponse();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[VcdiffShim] 差分合并失败");
            return BuildErrorResponse(ex.ToString());
        }
    }

    private void ApplyVcdiffInternal(string sourceFile, byte[] deltaData, string targetFile, string expectedMd5, long expectedSize)
    {
        var targetParentDir = Path.GetDirectoryName(targetFile) ?? throw new InvalidOperationException();
        Directory.CreateDirectory(targetParentDir);

        var tempPath = string.Concat(targetFile, TEMP_EXTENSION);
        var complete = false;

        try
        {
            Log.Information
            (
                "[VcdiffShim] 差分合并开始, 源 {SourceFile}, 差分大小 {DeltaSize}, 目标 {TargetFile}, 临时文件 {TempPath}, 期望大小 {ExpectedSize}",
                sourceFile,
                deltaData.Length,
                targetFile,
                tempPath,
                expectedSize
            );

            var mergeTicks = Stopwatch.GetTimestamp();
            Log.Information("[VcdiffShim] 正在合并差分, 源 {SourcePath}, 临时目标 {TempPath}", sourceFile, tempPath);
            RunXdeltaHelper(sourceFile, deltaData, tempPath);
            Log.Information("[VcdiffShim] 差分合并完成, 耗时 {ElapsedMs} ms", Stopwatch.GetElapsedTime(mergeTicks).TotalMilliseconds);

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

    private void RunXdeltaHelper(string sourceFile, byte[] deltaData, string tempPath)
    {
        var handle = GCHandle.Alloc(deltaData, GCHandleType.Pinned);

        try
        {
            var result = decodeWithDeltaMemory
            (
                sourceFile,
                handle.AddrOfPinnedObject(),
                (nuint)deltaData.Length,
                tempPath
            );

            if (result == 0)
                return;

            var sourceInfo  = new FileInfo(sourceFile);
            var nativeError = Marshal.PtrToStringUTF8(getLastError()) ?? string.Empty;
            Log.Error
            (
                "[VcdiffShim] 差分合并失败, 返回码 {Result}, 原因 {NativeError}, 源 {SourcePath} (大小 {SourceSize}), 差分大小 {DeltaSize}, 临时目标 {TempPath}",
                result,
                nativeError,
                sourceFile,
                sourceInfo.Length,
                deltaData.Length,
                tempPath
            );
            throw new InvalidDataException($"V3 差分合并失败: 返回码 {result}, 原因 {nativeError}, 源文件 {sourceInfo.Length} 字节, 差分 {deltaData.Length} 字节");
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XdeltaDecodeFileWithDeltaMemoryUtf8Delegate
    (
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourcePath,
        IntPtr                                      deltaData,
        nuint                                       deltaSize,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string targetPath
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr XdeltaBridgeGetLastErrorUtf8Delegate();

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

    #region Constants

    private const int    VCDIFF_OPCODE  = 0;
    private const int    RESULT_PASS    = 0;
    private const int    RESULT_ERROR   = 2;
    private const string TEMP_EXTENSION = ".tmp";

    #endregion
}
