using System;
using System.Diagnostics;
using System.IO;
using XIVLauncher.Common.Runtime;

namespace XIVLauncher.Common.Patching;

public static class PatchInstallerProcessStartInfo
{
    public static ProcessStartInfo Create(string executablePath, string arguments, bool asAdmin, string? dotnetRootPath)
    {
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var hasDotNetRoot    = !string.IsNullOrWhiteSpace(dotnetRootPath);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            Arguments        = arguments,
            UseShellExecute  = asAdmin,
            WorkingDirectory = workingDirectory
        };

        if (asAdmin)
            startInfo.Verb = "runas";

        if (hasDotNetRoot)
            ApplyEnvironment(startInfo, dotnetRootPath!, asAdmin);

        return startInfo;
    }

    public static string GetDefaultDotNetRootPath() =>
        DotNetRuntimeManager.GetRuntimeDirectory("win-x86").FullName;

    private static void ApplyEnvironment(ProcessStartInfo startInfo, string dotnetRootPath, bool asAdmin)
    {
        if (asAdmin)
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT_X86",     dotnetRootPath);
            Environment.SetEnvironmentVariable("DOTNET_ROOT(x86)",    dotnetRootPath);
            return;
        }

        startInfo.Environment["DOTNET_ROOT_X86"]          = dotnetRootPath;
        startInfo.Environment["DOTNET_ROOT(x86)"]         = dotnetRootPath;
        startInfo.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
    }
}
