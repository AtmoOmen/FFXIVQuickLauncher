using System;
using System.IO;
using System.Linq;
using Serilog;

namespace XIVLauncher.PatchInstaller.Utilities;

internal static class CommandPathArgumentNormalizer
{
    public static (string TargetDirectoryPath, string[] InputPaths) Normalize
    (
        string   targetDirectoryPath,
        string[] inputPaths,
        string   patchExtension
    )
    {
        if (!File.Exists(targetDirectoryPath) || !targetDirectoryPath.EndsWith(patchExtension, StringComparison.OrdinalIgnoreCase))
            return (targetDirectoryPath, inputPaths);

        var lastInputPath = inputPaths[^1];
        if (!Directory.Exists(lastInputPath)
            && !lastInputPath.EndsWith("/", StringComparison.Ordinal)
            && !lastInputPath.EndsWith("\\", StringComparison.Ordinal))
            return (targetDirectoryPath, inputPaths);

        Log.Information("Taking the first argument as the first patch file, and the last argument as the target directory.");
        return (lastInputPath, [targetDirectoryPath, ..inputPaths.Take(inputPaths.Length - 1)]);
    }
}
