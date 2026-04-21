using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.PatchInstaller.Utilities;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexRepairCommand
{
    public static readonly Command COMMAND = new("index-repair", "Repair a game installation.");

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
        COMMAND.Arguments.Add(PatchIndexFileArgument);
        COMMAND.Arguments.Add(GameRootPathArgument);
        COMMAND.Arguments.Add(PatchRootPathArgument);
        COMMAND.Options.Add(ThreadCountOption);
        COMMAND.SetAction(parseResult => new IndexRepairCommand(parseResult).Handle());
    }

    private IndexRepairCommand(ParseResult parseResult)
    {
        patchIndexFile = parseResult.GetValue(PatchIndexFileArgument)!;
        gameRootPath   = parseResult.GetValue(GameRootPathArgument)!;
        patchRootPath  = parseResult.GetValue(PatchRootPathArgument)!;
        threadCount    = ThreadCountResolver.Resolve(parseResult.GetValue(ThreadCountOption));
    }

    private async Task<int> Handle()
    {
        await IndexedZiPatchOperations.RepairFromPatchFileIndexFromFile(patchIndexFile, gameRootPath, patchRootPath, threadCount);
        return 0;
    }
}
