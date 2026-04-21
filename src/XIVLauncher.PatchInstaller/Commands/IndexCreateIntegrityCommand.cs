using System;
using System.Buffers;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Integrity;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.PatchInstaller.Utilities;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexCreateIntegrityCommand
{
    public static readonly Command COMMAND = new("index-create-integrity", "Create integrity check data for a game installation.");

    private static readonly Argument<string> PatchRootPathArgument = new("patch-root-path")
    {
        Description = "Path to a folder containing relevant patch files."
    };

    private static readonly Argument<string[]> PatchIndexFilesArgument = new("patch-index-files")
    {
        Description = "Path to a patch index file. (*.patch.index)",
        Arity = ArgumentArity.OneOrMore
    };

    private static readonly Option<int?> ThreadCountOption = new("--threads")
    {
        Description = "Number of threads. Specifying 0 will use all available cores.",
        Aliases = { "-t" }
    };

    private readonly string   patchRootPath;
    private readonly string[] patchIndexFiles;
    private readonly int      threadCount;

    static IndexCreateIntegrityCommand()
    {
        COMMAND.Arguments.Add(PatchRootPathArgument);
        COMMAND.Arguments.Add(PatchIndexFilesArgument);
        COMMAND.Options.Add(ThreadCountOption);
        COMMAND.SetAction((parseResult, cancellationToken) => new IndexCreateIntegrityCommand(parseResult).Handle(cancellationToken));
    }

    private IndexCreateIntegrityCommand(ParseResult parseResult)
    {
        patchRootPath   = parseResult.GetValue(PatchRootPathArgument)!;
        patchIndexFiles = parseResult.GetValue(PatchIndexFilesArgument)!;
        threadCount     = ThreadCountResolver.Resolve(parseResult.GetValue(ThreadCountOption));
    }

    private async Task<int> Handle(CancellationToken cancellationToken)
    {
        var patchGame = Path.Combine(patchRootPath, "game");

        if (!Directory.Exists(patchGame))
        {
            Log.Error("Directory does not exist: {path}", patchGame);
            return -1;
        }

        var patchFiles = new Dictionary<int, Dictionary<string, string>>();

        foreach (var dirPath in Directory.GetDirectories(patchGame))
        {
            var dirName = Path.GetFileName(dirPath);

            if (dirName.StartsWith("ex", StringComparison.OrdinalIgnoreCase))
            {
                patchFiles[int.Parse(dirName.Substring(2))] =
                    Directory.GetFiles(Directory.GetDirectories(dirPath).Single())
                             .ToDictionary(Path.GetFileName, x => x);
            }
            else if (dirName.Length == 8 && Regex.IsMatch(dirName, "[0-9a-f]{8}"))
                patchFiles[0] = Directory.GetFiles(dirPath).ToDictionary(Path.GetFileName, x => x);
        }

        using var cts             = new CancellationTokenSource();
        var       ct              = cts.Token;
        using var ctsRegistration = cancellationToken.Register(cts.Cancel);

        var tasks = new HashSet<Task<Tuple<string, string>>>();

        var result = new IntegrityCheckResult();

        try
        {
            foreach (var f in patchIndexFiles)
            {
                ct.ThrowIfCancellationRequested();
                var pi = new IndexedZiPatchIndex(new BinaryReader(new DeflateStream(new FileStream(f, FileMode.Open, FileAccess.Read), CompressionMode.Decompress)));

                switch (pi.ExpacVersion)
                {
                    case < 0:
                        Log.Information("Skipping boot patch index: {file}", f);
                        continue;

                    case 0:
                        result.GameVersion     = pi.VersionName;
                        result.LastGameVersion = Path.GetFileNameWithoutExtension(pi.Sources[^2][1..]);
                        result.Hashes[@"\game\ffxivgame.bck"] =
                            result.Hashes[@"\game\ffxivgame.ver"] =
                                HashFromBytes(Encoding.UTF8.GetBytes(pi.VersionName));
                        break;

                    default:
                        result.Hashes[$@"\game\sqpack\ex{pi.ExpacVersion}\ex{pi.ExpacVersion}.bck"] =
                            result.Hashes[$@"\game\sqpack\ex{pi.ExpacVersion}\ex{pi.ExpacVersion}.ver"] =
                                HashFromBytes(Encoding.UTF8.GetBytes(pi.VersionName));
                        break;
                }

                for (var i = 0; i < pi.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    while (tasks.Count >= threadCount)
                    {
                        ct.ThrowIfCancellationRequested();

                        var t = await Task.WhenAny(tasks);
                        tasks.Remove(t);

                        var (filename, hash)    = await t;
                        result.Hashes[filename] = hash;
                        Log.Information("Hashed: {name} => {hash}", filename, hash);
                    }

                    var sources = new List<Stream>();

                    try
                    {
                        // No linq; we need to dispose if it failed during the loop
                        foreach (var sourceName in pi.Sources)
                            sources.Add(File.OpenRead(patchFiles[pi.ExpacVersion][sourceName]));
                    }
                    catch (Exception)
                    {
                        foreach (var s in sources)
                            s.Dispose();
                        throw;
                    }

                    var fs   = pi[i].ToStream(sources);
                    var name = @"\game\" + pi[i].RelativePath.Replace("/", "\\");

                    Log.Information("Hashing: {name} ({progress}/{max}) of {ex}", name, i + 1, pi.Length, pi.ExpacVersion == 0 ? "base" : $"ex{pi.ExpacVersion}");
                    // Use empty cancellation token, since we need the task body to dispose the stream
                    tasks.Add(Task.Run(async () => Tuple.Create(name, await HashFromStream(fs, ct)), new()));
                }
            }

            while (tasks.Any())
            {
                ct.ThrowIfCancellationRequested();

                var t = await Task.WhenAny(tasks);
                tasks.Remove(t);

                var (filename, hash)    = await t;
                result.Hashes[filename] = hash;
                Log.Information("Hashed: {name} => {hash}", filename, hash);
            }
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(tasks);
        }

        result.Hashes = new SortedDictionary<string, string>(result.Hashes, new PathDepthComparer()).ToDictionary(x => x.Key, x => x.Value);
        Console.WriteLine(JsonConvert.SerializeObject(result, Formatting.Indented));

        return 0;
    }

    private string HashFromBytes(byte[] bytes) =>
        HashFromBytes(bytes, 0, bytes.Length);

    private string HashFromBytes(byte[] bytes, int offset, int length)
    {
        using var sha1 = SHA1.Create();

        sha1.Initialize();
        sha1.TransformFinalBlock(bytes, offset, length);
        return Sha1HashFormatter.Format(sha1.Hash);
    }

    private async Task<string> HashFromStream(Stream stream, CancellationToken cancellationToken)
    {
        var buf = ArrayPool<byte>.Shared.Rent(65536);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sha1 = SHA1.Create();

            sha1.Initialize();
            var remaining = stream.Length;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var r = (int)Math.Min(buf.Length, remaining);
                if (r != await stream.ReadAsync(buf, 0, r, cancellationToken))
                    throw new IOException("Failed to read wholly");

                sha1.TransformBlock(buf, 0, r, null, 0);
                remaining -= r;
            }

            sha1.TransformFinalBlock([], 0, 0);

            return Sha1HashFormatter.Format(sha1.Hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
            stream.Dispose();
        }
    }

    private class PathDepthComparer : IComparer<string>
    {
        public int Compare(string l, string r)
        {
            int comp;
            var llist = l.Split('\\').Reverse().ToList();
            var rlist = r.Split('\\').Reverse().ToList();

            while (llist.Count > 1 && rlist.Count > 1)
            {
                var lback = llist[llist.Count - 1];
                var rback = rlist[rlist.Count - 1];
                comp = string.Compare(lback, rback, StringComparison.OrdinalIgnoreCase);
                if (comp == 0)
                    comp = string.Compare(lback, rback, StringComparison.Ordinal);
                if (comp != 0)
                    return comp;

                llist.RemoveAt(llist.Count - 1);
                rlist.RemoveAt(rlist.Count - 1);
            }

            comp = llist.Count.CompareTo(rlist.Count);

            if (comp == 0 && llist.Any() && rlist.Any())
            {
                comp = string.Compare(llist[llist.Count - 1], rlist[rlist.Count - 1], StringComparison.OrdinalIgnoreCase);
                if (comp == 0)
                    comp = string.Compare(llist[llist.Count - 1], rlist[rlist.Count - 1], StringComparison.Ordinal);
            }

            return comp;
        }
    }
}
