using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.PatchInstaller.Utilities;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexVerifyCommand
{
    public static readonly Command COMMAND = new("index-verify", "Verify and optionally repair a game installation.");

    private static readonly Argument<string> GameRootPathArgument = new("game-path")
    {
        Description = "Root folder of a game installation, such as \"C:\\Program Files (x86)\\SquareEnix\\FINAL FANTASY XIV - A Realm Reborn\""
    };

    private static readonly Argument<string[]> PatchIndexFilesArgument = new("patch-index-files")
    {
        Description = "Path to a patch index file. (*.patch.index)"
    };

    private static readonly Option<int?> ThreadCountOption = new("--threads")
    {
        Description = "Number of threads. Specifying 0 will use all available cores.",
        Aliases = { "-t" }
    };

    private readonly string   gameRootPath;
    private readonly string[] patchIndexFiles;
    private readonly int      threadCount;

    static IndexVerifyCommand()
    {
        COMMAND.Arguments.Add(GameRootPathArgument);
        COMMAND.Arguments.Add(PatchIndexFilesArgument);
        COMMAND.Options.Add(ThreadCountOption);
        COMMAND.SetAction((parseResult, cancellationToken) => new IndexVerifyCommand(parseResult).Handle(cancellationToken));
    }

    private IndexVerifyCommand(ParseResult parseResult)
    {
        gameRootPath    = parseResult.GetValue(GameRootPathArgument)!;
        patchIndexFiles = parseResult.GetValue(PatchIndexFilesArgument)!;
        threadCount                  = ThreadCountResolver.Resolve(parseResult.GetValue(ThreadCountOption));
        (gameRootPath, patchIndexFiles) = CommandPathArgumentNormalizer.Normalize(gameRootPath, patchIndexFiles, ".patch.index");
    }

    private async Task<int> Handle(CancellationToken cancellationToken)
    {
        foreach (var f in patchIndexFiles)
            await IndexedZiPatchOperations.VerifyFromZiPatchIndex(f, gameRootPath, threadCount, cancellationToken);
        return 0;
    }
}
