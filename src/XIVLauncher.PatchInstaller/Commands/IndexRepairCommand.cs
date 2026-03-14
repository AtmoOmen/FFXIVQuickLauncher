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

    private static readonly Argument<string> PatchIndexFileArgument = new("patch-index-file", "Path to a patch index file. (*.patch.index)");

    private static readonly Argument<string> GameRootPathArgument = new
    (
        "game-path",
        "Root folder of a game installation, such as \"C:\\Program Files (x86)\\SquareEnix\\FINAL FANTASY XIV - A Realm Reborn\\\""
    );

    private static readonly Argument<string> PatchRootPathArgument = new("patch-root-path", "Path to a folder containing relevant patch files.");

    private static readonly Option<int> ThreadCountOption = new
    (
        ["-t", "--threads"],
        () => Math.Min(Environment.ProcessorCount, 8),
        "Number of threads. Specifying 0 will use all available cores."
    );

    private readonly string patchIndexFile;
    private readonly string gameRootPath;
    private readonly string patchRootPath;
    private readonly int    threadCount;

    static IndexRepairCommand()
    {
        Command.AddArgument(PatchIndexFileArgument);
        Command.AddArgument(GameRootPathArgument);
        Command.AddArgument(PatchRootPathArgument);
        ThreadCountOption.AddValidator(x => x.ErrorMessage = x.GetValueOrDefault<int>() >= 0 ? null : "Must be 0 or more");
        Command.AddOption(ThreadCountOption);
        Command.SetHandler(x => new IndexRepairCommand(x.ParseResult).Handle());
    }

    private IndexRepairCommand(ParseResult parseResult)
    {
        patchIndexFile = parseResult.GetValueForArgument(PatchIndexFileArgument);
        gameRootPath   = parseResult.GetValueForArgument(GameRootPathArgument);
        patchRootPath  = parseResult.GetValueForArgument(PatchRootPathArgument);
        threadCount    = parseResult.GetValueForOption(ThreadCountOption);
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
