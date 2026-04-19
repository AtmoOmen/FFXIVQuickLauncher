using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using XIVLauncher.Support.Velopack;

namespace XIVLauncher.Startup.Steps;

public class VelopackStep : IStartupStep
{
    public string Name  => "Velopack 初始化";
    public int    Order => 60;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        VelopackApp.Build()
                   .WithAfterUpdateFastCallback(_ => RestartElevated())
                   .Run();
        return Task.CompletedTask;
    }

    private static void RestartElevated()
    {
        var restartState = VelopackRestartStateStore.Load();
        if (restartState == null)
            return;

        var executablePath = Path.GetFullPath(restartState.ExecutableRelativePath, AppContext.BaseDirectory);
        if (!File.Exists(executablePath))
            return;

        var arguments = string.Join(" ", restartState.Arguments.Select(EncodeParameterArgument));
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb            = "runas",
            Arguments       = arguments
        };

        Process.Start(startInfo);
        VelopackRestartStateStore.Delete();
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
