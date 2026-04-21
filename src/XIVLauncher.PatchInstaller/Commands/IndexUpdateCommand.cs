using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.PatchInstaller.Commands.Internal;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexUpdateCommand
{
    public static readonly Command COMMAND = new("index-update", "Update patch index files from internet.");

    private static readonly Option<string?> PatchRootPathOption = new("--patch-root-path")
    {
        Description = "Root directory of patch file hierarchy. Defaults to a directory under the temp directory of the current user.",
        Aliases = { "-r" }
    };

    private readonly TempSettings settings;

    static IndexUpdateCommand()
    {
        COMMAND.Options.Add(PatchRootPathOption);
        COMMAND.SetAction((parseResult, cancellationToken) => new IndexUpdateCommand(parseResult).Handle(cancellationToken));
    }

    private IndexUpdateCommand(ParseResult parseResult)
    {
        settings = new
        (
            new
            (
                parseResult.GetValue(PatchRootPathOption)
                ?? Path.Combine(Path.GetTempPath(), "XIVLauncher.PatchInstaller")
            )
        );
    }

    private async Task<int> Handle(CancellationToken cancellationToken)
    {
        if (!settings.GamePath.Exists)
            settings.GamePath.Create();
        if (!settings.PatchPath.Exists)
            settings.PatchPath.Create();
        var launcher = new Launcher();

        var areas = await LoginArea.Get();
        var area  = areas[Random.Shared.Next(areas.Length)];

        var loginResult = await launcher.UpdateClient.Check(area, settings.GamePath, false);
        var gamePatchListFile = new FileInfo(Path.Combine(settings.GamePath.FullName, "gamelist.json"));
        File.WriteAllText(gamePatchListFile.FullName, JsonConvert.SerializeObject(loginResult.V3GameUpdatePlan, Formatting.Indented));

        using var metadataClient = new V3PatchIndexMetadataClient();
        var latestVersions = await metadataClient.DownloadLatestVersionsAsync(cancellationToken);

        if (loginResult.V3GameUpdatePlan is { } updatePlan)
            Log.Information("Detected V3 update plan: {current} => {target}", updatePlan.CurrentGameVersion, updatePlan.TargetGameVersion);
        else
            Log.Information("No V3 update required for current game version.");

        foreach (var (repository, versionInfo) in latestVersions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patchIndexFile = await metadataClient.DownloadPatchIndexAsync(settings.PatchPath, repository, versionInfo.Version, versionInfo.Revision, cancellationToken);
            Log.Information("Downloaded patch index: {repo} => {path}", repository, patchIndexFile.FullName);
        }

        return 0;
    }

    private class TempSettings : ISettings
    {
        public string             AcceptLanguage          => "en-US";
        public ClientLanguage?    ClientLanguage          => Common.ClientLanguage.English;
        public bool?              KeepPatches             => true;
        public DirectoryInfo      PatchPath               { get; }
        public DirectoryInfo      GamePath                => PatchPath;
        public AcquisitionMethod? PatchAcquisitionMethod  => AcquisitionMethod.NetDownloader;
        public int                DalamudInjectionDelayMs => 0;
        public long               SpeedLimitBytes         { get; set; }

        public TempSettings(DirectoryInfo patchPath) =>
            PatchPath = patchPath;
    }
}
