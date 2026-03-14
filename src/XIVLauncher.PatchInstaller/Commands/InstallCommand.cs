using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Patching;

namespace XIVLauncher.PatchInstaller.Commands;

public class InstallCommand
{
    public static readonly Command Command = new("install", "Install the given patch files in the specified order.");

    private static readonly Argument<string> GameRootPathArgument = new
    (
        "game-root",
        "Path to a game installation, such as \"C:\\Program Files (x86)\\SquareEnix\\FINAL FANTASY XIV - A Realm Reborn\\\""
    );

    private static readonly Argument<string[]> PatchFilesArgument = new("patch-file", "Path to patch file(s).")
    {
        Arity = ArgumentArity.OneOrMore
    };

    private readonly string   gameRootPath;
    private readonly string[] patchFiles;

    static InstallCommand()
    {
        Command.AddArgument(GameRootPathArgument);
        Command.AddArgument(PatchFilesArgument);
        Command.SetHandler(x => new InstallCommand(x.ParseResult).Handle());
    }

    private InstallCommand(ParseResult parseResult)
    {
        gameRootPath = parseResult.GetValueForArgument(GameRootPathArgument);
        patchFiles   = parseResult.GetValueForArgument(PatchFilesArgument);

        // Do we have a .patch as the first argument?
        if (File.Exists(gameRootPath) && gameRootPath.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
        {
            var lastArg = patchFiles[patchFiles.Length - 1];

            // Do we have a folder as the last argument?
            if (Directory.Exists(lastArg) || lastArg.EndsWith("/", StringComparison.Ordinal) || lastArg.EndsWith("\\", StringComparison.Ordinal))
            {
                Log.Information("Taking the first argument as the first patch file, and the last argument as the target directory.");
                patchFiles   = new[] { gameRootPath }.Concat(patchFiles.Take(patchFiles.Length - 1)).ToArray();
                gameRootPath = lastArg;
            }
        }
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
