using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using XIVLauncher.Common.Constant;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Startup.Elevation;

internal static class StartupElevationService
{
    public static bool TryRestartElevatedAndExit()
    {
        if (PlatformHelpers.IsElevated())
            return false;

        var executablePath = Paths.ResolveExecutablePath();
        var arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(EncodeParameterArgument));
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = arguments
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (PlatformHelpers.IsWindowsErrorCancelled(ex))
        {
            return false;
        }

        Environment.Exit(0);
        return true;
    }

    private static string EncodeParameterArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(" \t\n\v\"".ToCharArray()) == -1)
            return argument;

        var quoted = new System.Text.StringBuilder(argument.Length * 2);
        quoted.Append('\"');

        var numberBackslashes = 0;

        foreach (var chr in argument)
        {
            switch (chr)
            {
                case '\\':
                    numberBackslashes++;
                    continue;

                case '\"':
                    quoted.Append('\\', numberBackslashes * 2 + 1);
                    quoted.Append(chr);
                    break;

                default:
                    quoted.Append('\\', numberBackslashes);
                    quoted.Append(chr);
                    break;
            }

            numberBackslashes = 0;
        }

        quoted.Append('\\', numberBackslashes * 2);
        quoted.Append('\"');

        return quoted.ToString();
    }
}
