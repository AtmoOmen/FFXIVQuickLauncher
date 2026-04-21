using System.CommandLine;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using XIVLauncher.Common.Patching.IndexedZiPatch;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexCreateCommand
{
    public static readonly Command COMMAND = new("index-create", "Create patch index files according to a patch chain specified from arguments.");

    private static readonly Argument<int> ExpacVersionArgument = new("expac-version")
    {
        Description = "Expansion pack version in an integer. -1 = boot, 0 = base game, 1 = Heavensward, etc."
    };

    private static readonly Argument<string[]> PatchFilesArgument = new("patch-file")
    {
        Description = "Path to patch file(s).",
        Arity = ArgumentArity.OneOrMore
    };

    private readonly int      expacVersion;
    private readonly string[] patchFiles;

    static IndexCreateCommand()
    {
        COMMAND.Arguments.Add(ExpacVersionArgument);
        COMMAND.Arguments.Add(PatchFilesArgument);
        COMMAND.SetAction(parseResult => new IndexCreateCommand(parseResult).Handle());
    }

    private IndexCreateCommand(ParseResult parseResult)
    {
        expacVersion = parseResult.GetValue(ExpacVersionArgument);
        patchFiles   = parseResult.GetValue(PatchFilesArgument)!;
    }

    private async Task<int> Handle()
    {
        await IndexedZiPatchOperations.CreateZiPatchIndices(expacVersion, patchFiles);
        return 0;
    }
}
