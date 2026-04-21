using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading.Tasks;
using XIVLauncher.Common.Patching;
using XIVLauncher.PatchInstaller.Utilities;

namespace XIVLauncher.PatchInstaller.Commands;

public class InstallCommand
{
    public static readonly Command COMMAND = new("install", "Install the given patch files in the specified order.");

    private static readonly Argument<string> GameRootPathArgument = new("game-root")
    {
        Description = "Path to a game installation, such as \"C:\\Program Files (x86)\\SquareEnix\\FINAL FANTASY XIV - A Realm Reborn\\\""
    };

    private static readonly Argument<string[]> PatchFilesArgument = new("patch-file")
    {
        Description = "Path to patch file(s).",
        Arity = ArgumentArity.OneOrMore
    };

    private readonly string   gameRootPath;
    private readonly string[] patchFiles;

    static InstallCommand()
    {
        COMMAND.Arguments.Add(GameRootPathArgument);
        COMMAND.Arguments.Add(PatchFilesArgument);
        COMMAND.SetAction(parseResult => new InstallCommand(parseResult).Handle());
    }

    private InstallCommand(ParseResult parseResult)
    {
        gameRootPath = parseResult.GetValue(GameRootPathArgument)!;
        patchFiles   = parseResult.GetValue(PatchFilesArgument)!;
        (gameRootPath, patchFiles) = CommandPathArgumentNormalizer.Normalize(gameRootPath, patchFiles, ".patch");
    }

    private Task<int> Handle()
    {
        foreach (var file in patchFiles)
        {
            var fi = new FileInfo(file);
            if (!fi.Exists)
                throw new FileNotFoundException("File not found", file);
            if (fi.Length == 0)
                throw new FileFormatException($"File is empty: {file}");
        }

        foreach (var file in patchFiles)
            RemotePatchInstaller.InstallPatch(file, gameRootPath);
        return Task.FromResult(0);
    }
}
