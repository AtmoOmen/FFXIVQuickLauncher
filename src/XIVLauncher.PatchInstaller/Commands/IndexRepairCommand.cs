using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Threading.Tasks;
using XIVLauncher.Common.Patching.IndexedZiPatch;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexRepairCommand
{
    public static readonly Command Command = new("index-repair", "Repair a game installation.");

    private static readonly Argument<string> PatchIndexFileArgument = new("patch-index-file")
    {
        Description = "Path to a patch index file. (*.patch.index)"
    };

    private static readonly Argument<string> GameRootPathArgument = new("game-path")
    {
        Description = "Root folder of a game installation, such as \"C:\\Program Files (x86)\\SquareEnix\\FINAL FANTASY XIV - A Realm Reborn\\\""
    };

    private static readonly Argument<string> PatchRootPathArgument = new("patch-root-path")
    {
        Description = "Path to a folder containing relevant patch files."
    };

    private static readonly Option<int?> ThreadCountOption = new("--threads")
    {
        Description = "Number of threads. Specifying 0 will use all available cores.",
        Aliases = { "-t" }
    };

    private readonly string patchIndexFile;
    private readonly string gameRootPath;
    private readonly string patchRootPath;
    private readonly int    threadCount;

    static IndexRepairCommand()
    {
        Command.Arguments.Add(PatchIndexFileArgument);
        Command.Arguments.Add(GameRootPathArgument);
        Command.Arguments.Add(PatchRootPathArgument);
        Command.Options.Add(ThreadCountOption);
        Command.SetAction(parseResult => new IndexRepairCommand(parseResult).Handle());
    }

    private IndexRepairCommand(ParseResult parseResult)
    {
        patchIndexFile = parseResult.GetValue(PatchIndexFileArgument)!;
        gameRootPath   = parseResult.GetValue(GameRootPathArgument)!;
        patchRootPath  = parseResult.GetValue(PatchRootPathArgument)!;
        threadCount    = parseResult.GetValue(ThreadCountOption) ?? Math.Min(Environment.ProcessorCount, 8);
        if (threadCount < 0)
            throw new ArgumentOutOfRangeException(nameof(threadCount), "Must be 0 or more");
        if (threadCount == 0)
            threadCount = Environment.ProcessorCount;
        Debug.Assert(threadCount > 0);
    }

    private async Task<int> Handle()
    {
        await IndexedZiPatchOperations.RepairFromPatchFileIndexFromFile(patchIndexFile, gameRootPath, patchRootPath, threadCount);
        return 0;
    }
}
