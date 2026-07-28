using System.IO;
using System.Text;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Dalamud;
using XIVLauncher.GamePatchV3.Integrity;
using XIVLauncher.GamePatchV3.Integrity.Models;

namespace XIVLauncher.Support;

/// <summary>
///     Class responsible for printing troubleshooting information to the log.
/// </summary>
public static class Troubleshooting
{
    /// <summary>
    ///     Gets the most recent exception to occur.
    /// </summary>
    public static Exception LastException { get; private set; } = null!;

    /// <summary>
    ///     Log the last exception in a parseable format to serilog.
    /// </summary>
    /// <param name="exception">The exception to log.</param>
    /// <param name="context">Additional context.</param>
    public static void LogException(Exception exception, string context)
    {
        LastException = exception;

        try
        {
            var fixedContext = context?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

            var payload = new ExceptionPayload
            {
                Context = fixedContext!,
                When    = DateTime.Now,
                Info    = exception.ToString()
            };

            var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
            Log.Information($"LASTEXCEPTION:{encodedPayload}");
        }
        catch (Exception)
        {
            Log.Error("Could not print exception");
        }
    }

    internal static string GetTroubleshootingJson(DirectoryInfo? gamePath)
    {
        var integrity = gamePath == null
                            ? TroubleshootingPayload.IndexIntegrityResult.NoGame
                            : TroubleshootingPayload.IndexIntegrityResult.Success;

        try
        {
            if (gamePath == null || !gamePath.Exists || gamePath.GetDirectories().All(x => x.Name != "game"))
                integrity = TroubleshootingPayload.IndexIntegrityResult.NoGame;
            else
            {
                var result = GameIntegrityChecker.CompareIntegrityAsync(null!, gamePath, true).Result;

                integrity = result.CompareResult switch
                {
                    IntegrityCheckCompareResult.ReferenceFetchFailure => TroubleshootingPayload.IndexIntegrityResult.ReferenceFetchFailure,
                    IntegrityCheckCompareResult.ReferenceNotFound     => TroubleshootingPayload.IndexIntegrityResult.ReferenceNotFound,
                    IntegrityCheckCompareResult.Invalid               => TroubleshootingPayload.IndexIntegrityResult.Failed,
                    _                                                 => integrity
                };
            }
        }
        catch (Exception)
        {
            integrity = TroubleshootingPayload.IndexIntegrityResult.Exception;
        }

        string GetVersion(Repository repository, bool isBackup = false) =>
            gamePath == null ? string.Empty : repository.GetVer(gamePath, isBackup);

        var ffxivVer    = GetVersion(Repository.Ffxiv);
        var ffxivVerBck = GetVersion(Repository.Ffxiv, true);
        var ex1Ver      = GetVersion(Repository.Ex1);
        var ex1VerBck   = GetVersion(Repository.Ex1, true);
        var ex2Ver      = GetVersion(Repository.Ex2);
        var ex2VerBck   = GetVersion(Repository.Ex2, true);
        var ex3Ver      = GetVersion(Repository.Ex3);
        var ex3VerBck   = GetVersion(Repository.Ex3, true);
        var ex4Ver      = GetVersion(Repository.Ex4);
        var ex4VerBck   = GetVersion(Repository.Ex4, true);
        var ex5Ver      = GetVersion(Repository.Ex5);
        var ex5VerBck   = GetVersion(Repository.Ex5, true);

        var payload = new TroubleshootingPayload
        {
            When                  = DateTime.Now,
            DalamudEnabled        = App.Settings.DalamudEnabled,
            DalamudLoadMethod     = App.Settings.DalamudLoadMethod,
            DalamudInjectionDelay = App.Settings.DalamudInjectionDelayMS,
            EncryptArguments      = App.Settings.EncryptArgumentsV2,
            LauncherVersion       = AppUtil.GetAssemblyVersion()!,
            LauncherHash          = AppUtil.GetGitHash()!,
            Official              = AppUtil.GetBuildOrigin() == "AtmoOmen/FFXIVQuickLauncher",
            DpiAwareness          = App.Settings.DPIAwareness,

            ObservedGameVersion = ffxivVer,
            ObservedEx1Version  = ex1Ver,
            ObservedEx2Version  = ex2Ver,
            ObservedEx3Version  = ex3Ver,
            ObservedEx4Version  = ex4Ver,
            ObservedEx5Version  = ex5Ver,

            BckMatch = ffxivVer == ffxivVerBck && ex1Ver == ex1VerBck && ex2Ver == ex2VerBck && ex3Ver == ex3VerBck && ex4Ver == ex4VerBck && ex5Ver == ex5VerBck,

            IndexIntegrity = integrity
        };

        return JsonConvert.SerializeObject(payload);
    }

    /// <summary>
    ///     Log troubleshooting information in a parseable format to Serilog.
    /// </summary>
    internal static void LogTroubleshooting(DirectoryInfo? gamePath)
    {
        try
        {
            var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(GetTroubleshootingJson(gamePath)));
            Log.Information($"TROUBLESHXLTING:{encodedPayload}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not print troubleshooting");
        }
    }

    private class ExceptionPayload
    {
        public required DateTime When { get; set; }

        public required string Info { get; set; }

        public required string Context { get; set; }
    }

    private class TroubleshootingPayload
    {
        public required DateTime When { get; set; }

        public required bool DalamudEnabled { get; set; }

        public required DalamudLoadMethod DalamudLoadMethod { get; set; }

        public required decimal DalamudInjectionDelay { get; set; }

        public required bool EncryptArguments { get; set; }

        public required string LauncherVersion { get; set; }

        public required string LauncherHash { get; set; }

        public required bool Official { get; set; }

        public required DPIAwareness DpiAwareness { get; set; }

        public required string ObservedGameVersion { get; set; }

        public required string ObservedEx1Version { get; set; }
        public required string ObservedEx2Version { get; set; }
        public required string ObservedEx3Version { get; set; }
        public required string ObservedEx4Version { get; set; }
        public required string ObservedEx5Version { get; set; }

        public required bool BckMatch { get; set; }

        public required IndexIntegrityResult IndexIntegrity { get; set; }

        public enum IndexIntegrityResult
        {
            Failed,
            Exception,
            NoGame,
            ReferenceNotFound,
            ReferenceFetchFailure,
            Success
        }
    }
}
