using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using StreamJsonRpc;
using XIVLauncher.Common.Constant;
using Serilog;

namespace XIVLauncher.Login;

public sealed class WeGameLoginCapturer
{
    private const string PIPE_NAME = "ApkalluCaller";

    public async Task<WeGameCaptureResult> CaptureAsync
    (
        string            sdologinDir,
        CancellationToken cancellationToken,
        IProgress<string> progress
    )
    {
        progress.Report("正在部署 version.dll");
        DeployVersionDll(sdologinDir);

        progress.Report("正在等待 WeGame 登录");

        var completionSource = new TaskCompletionSource<WeGameCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        NamedPipeServerStream pipe;
        try
        {
            pipe = new NamedPipeServerStream
            (
                PIPE_NAME,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous
            );
        }
        catch (IOException ex)
        {
            throw new WeGameCapturePipeBusyException(ex);
        }

        await using (pipe.ConfigureAwait(false))
        await using (cancellationToken.Register(() =>
                     {
                         completionSource.TrySetCanceled(cancellationToken);
                         try
                         {
                             pipe.Dispose();
                         }
                         catch
                         {
                         }
                     }).ConfigureAwait(false))
        {
            TryLaunchWeGame(progress);

            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

            var formatter = new SystemTextJsonFormatter();
            var handler   = new NewLineDelimitedMessageHandler(pipe, pipe, formatter);
            using var rpc = new JsonRpc(handler, new RpcHandler(completionSource));
            rpc.StartListening();

            var result = await completionSource.Task.ConfigureAwait(false);
            progress.Report("已捕获登录信息, 正在登录盛趣");

            try
            {
                await rpc.Completion.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }

            return result;
        }
    }

    public static void DeployVersionDll(string sdologinDir)
    {
        var sourcePath = Path.Combine(Paths.ResourcesPath, "version.dll");
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到 version.dll 资源", sourcePath);

        var destinationPath = Path.Combine(sdologinDir, "version.dll");
        if (File.Exists(destinationPath) && HashEquals(sourcePath, destinationPath))
        {
            Log.Information("version.dll 已最新, 跳过部署");
            return;
        }

        try
        {
            File.Copy(sourcePath, destinationPath, true);
            Log.Information("version.dll 已部署: {DestinationPath}", destinationPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new VersionDllPermissionDeniedException(sourcePath, destinationPath, ex);
        }
        catch (IOException ex) when (IsAccessDenied(ex))
        {
            throw new VersionDllPermissionDeniedException(sourcePath, destinationPath, ex);
        }
    }

    public static bool HashEquals(string sourcePath, string destinationPath)
    {
        using var sha = SHA256.Create();
        using var sourceStream = File.OpenRead(sourcePath);
        using var destinationStream = File.OpenRead(destinationPath);
        return sha.ComputeHash(sourceStream).SequenceEqual(sha.ComputeHash(destinationStream));
    }

    private static bool IsAccessDenied(IOException ex)
    {
        const int ERR_ACCESS_DENIED = 5;
        const int ERR_SHARING_VIOLATION = 32;
        var code = ex.HResult & 0xFFFF;
        return code == ERR_ACCESS_DENIED || code == ERR_SHARING_VIOLATION;
    }

    private static void TryLaunchWeGame(IProgress<string> progress)
    {
        try
        {
            Process.Start
            (
                new ProcessStartInfo
                {
                    FileName        = $"wegame://StartFor={WeGamePathValidator.FFXIV_WE_GAME_GAME_ID}",
                    UseShellExecute = true
                }
            );
            progress.Report("已请求启动 WeGame, 请在 WeGame 中登录最终幻想 14");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "wegame:// 唤起失败");
            progress.Report("自动唤起 WeGame 失败, 请手动启动 WeGame 并登录最终幻想 14");
        }
    }

    private sealed class RpcHandler(TaskCompletionSource<WeGameCaptureResult> completionSource)
    {
        [JsonRpcMethod("login.captured")]
        public string LoginCaptured(string userid, string token)
        {
            completionSource.TrySetResult(new(userid, token));
            return "ok";
        }
    }
}

internal sealed class VersionDllPermissionDeniedException(string sourcePath, string destinationPath, Exception innerException)
    : Exception($"无权限写入 {destinationPath}", innerException)
{
    public string SourcePath { get; } = sourcePath;

    public string DestinationPath { get; } = destinationPath;
}

internal sealed class WeGameCapturePipeBusyException(Exception innerException)
    : Exception("命名管道 ApkalluCaller 已被另一个进程占用", innerException);
