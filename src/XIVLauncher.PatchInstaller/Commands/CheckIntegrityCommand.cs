using System;
using System.Buffers;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Integrity;
using XIVLauncher.Common.Game.Patch.V3;
using XIVLauncher.PatchInstaller.Utilities;

namespace XIVLauncher.PatchInstaller.Commands;

public class CheckIntegrityCommand
{
    public static readonly Command COMMAND = new("check-integrity");

    private static readonly Argument<string> GameRootPathArgument = new("game-path")
    {
        Description = "Root folder of a game installation, such as \"C:\\Program Files (x86)\\SquareEnix\\FINAL FANTASY XIV - A Realm Reborn\""
    };

    private static readonly Option<string?> IntegrityFilePathOption = new("--integrity-file")
    {
        Description = $"Path to integrity check file. Leave it empty to download from: {new Uri(V3GamePatchConstants.REMOTE_VERSION_URL).Host}",
        Aliases = { "-f" }
    };

    private static readonly Option<bool> IndexOnlyOption = new("--index-only")
    {
        Description = $"Path to integrity check file. Leave it empty to download from: {new Uri(V3GamePatchConstants.REMOTE_VERSION_URL).Host}",
        Aliases = { "-i" }
    };

    private static readonly Option<int?> ThreadCountOption = new("--threads")
    {
        Description = "Number of threads. Specifying 0 will use all available cores.",
        Aliases = { "-t" }
    };

    private readonly string  gameRootPath;
    private readonly string? integrityFilePath;
    private readonly bool    indexOnly;
    private readonly int     threadCount;

    static CheckIntegrityCommand()
    {
        COMMAND.Arguments.Add(GameRootPathArgument);
        COMMAND.Options.Add(IntegrityFilePathOption);
        COMMAND.Options.Add(IndexOnlyOption);
        COMMAND.Options.Add(ThreadCountOption);
        COMMAND.SetAction((parseResult, cancellationToken) => new CheckIntegrityCommand(parseResult).Handle(cancellationToken));
    }

    private CheckIntegrityCommand(ParseResult parseResult)
    {
        gameRootPath      = parseResult.GetValue(GameRootPathArgument)!;
        integrityFilePath = parseResult.GetValue(IntegrityFilePathOption);
        indexOnly         = parseResult.GetValue(IndexOnlyOption);
        threadCount       = ThreadCountResolver.Resolve(parseResult.GetValue(ThreadCountOption));
    }

    /// <summary>
    ///     Runs given tasks, up to <paramref name="numThreads" /> concurrent tasks active.
    /// </summary>
    /// <param name="tasks">Functions that return tasks to be run.</param>
    /// <param name="numThreads">Number of active threads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <typeparam name="T">Return type.</typeparam>
    /// <returns>Enumerator for return values. Note that the order from <paramref name="tasks" /> is NOT preserved.</returns>
    /// <exception cref="AggregateException">If any of the tasks fail.</exception>
    private static async IAsyncEnumerable<T> RunThreadLimited<T>
    (
        IEnumerable<Func<CancellationToken, Task<T>>> tasks,
        int                                           numThreads,
        [EnumeratorCancellation] CancellationToken    cancellationToken
    )
    {
        using var cts          = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var       runningTasks = new List<Task<T>>(numThreads);

        foreach (var task in tasks)
        {
            while (runningTasks.Count >= numThreads)
                yield return await WaitOne(cts);

            runningTasks.Add(task(cts.Token));
        }

        while (runningTasks.Any())
            yield return await WaitOne(cts);

        yield break;

        async Task<T> WaitOne(CancellationTokenSource cts2)
        {
            var completedTask = await Task.WhenAny(runningTasks);

            if (completedTask.Status != TaskStatus.RanToCompletion)
            {
                cts2.Cancel();
                await Task.WhenAll(runningTasks);
                throw new AggregateException
                (
                    runningTasks
                        .Select(x => x.Exception)
                        .SelectMany(x => (IEnumerable<Exception>?)x?.InnerExceptions ?? Array.Empty<Exception>())
                );
            }

            var result = await completedTask;
            runningTasks.Remove(completedTask);
            return result;
        }
    }

    private async Task<int> Handle(CancellationToken cancellationToken)
    {
        IntegrityCheckResult icr;

        if (string.IsNullOrWhiteSpace(integrityFilePath))
        {
            var gameVersion = File.ReadAllText($@"{gameRootPath}\game\ffxivgame.ver");
            Log.Information("Downloading integrity check file for version: {verison}", gameVersion);
            icr = IntegrityCheck.DownloadIntegrityCheckForVersion().Result;
        }
        else
            icr = JsonConvert.DeserializeObject<IntegrityCheckResult>(File.ReadAllText(integrityFilePath))
                  ?? throw new InvalidDataException("Failed to deserialize integrity check file.");

        var fileCounter  = 0;
        var matchCounter = 0;

        var hashList = indexOnly
                           ? icr.Hashes.Where(x => Path.GetExtension(x.Key).ToLowerInvariant() == ".index").ToDictionary(x => x.Key, x => x.Value)
                           : icr.Hashes;

        await foreach (var validationResult in RunThreadLimited<(string Path, string? Hash, Exception? Exception)>
                       (
                           hashList.Keys.Select(CreateValidateFileTask),
                           threadCount,
                           cancellationToken
                       ))
        {
            var (path, calculatedHash, exception) = validationResult;
            var expectedHash = hashList[path];

            fileCounter++;
            matchCounter += expectedHash == calculatedHash ? 1 : 0;

            if (exception is not null)
                Log.Warning("[{counter}/{max}] {path}: {excType}: {excMsg}", fileCounter, hashList.Count, path, exception.GetType(), exception.Message);
            else if (expectedHash != calculatedHash)
                Log.Warning("[{counter}/{max}] {path}: Hashes do not match.", fileCounter, hashList.Count, path);
            else
                Log.Information("[{counter}/{max}] {path}: Verified.", fileCounter, hashList.Count, path);
        }

        Log.Information("{ok} file(s) out of {total} file(s) are verified to be correct.", matchCounter, fileCounter);

        return fileCounter - matchCounter;
    }

    private Func<CancellationToken, Task<(string Path, string? Hash, Exception? Exception)>> CreateValidateFileTask(string path)
    {
        return ct => Task.Run
        (
            async () =>
            {
                var        buf       = ArrayPool<byte>.Shared.Rent(65536);
                string?    hash      = null;
                Exception? exception = null;

                try
                {
                    ct.ThrowIfCancellationRequested();
                    using var stream = File.OpenRead($@"{gameRootPath}\{path}");
                    using var sha1   = SHA1.Create();

                    sha1.Initialize();
                    var remaining = stream.Length;

                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        var r = (int)Math.Min(buf.Length, remaining);
                        if (r != await stream.ReadAsync(buf, 0, r, ct))
                            throw new IOException("Failed to read wholly");

                        sha1.TransformBlock(buf, 0, r, null, 0);
                        remaining -= r;
                    }

                    sha1.TransformFinalBlock([], 0, 0);
                    hash = Sha1HashFormatter.Format(sha1.Hash);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    exception = e;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }

                return (path, hash, exception);
            },
            ct
        );
    }
}
