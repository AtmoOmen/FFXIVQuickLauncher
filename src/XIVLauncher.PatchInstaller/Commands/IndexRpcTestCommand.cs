using System;
using System.CommandLine;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Patching.IndexedZiPatch;
using XIVLauncher.PatchInstaller.Commands.Internal;

namespace XIVLauncher.PatchInstaller.Commands;

public class IndexRpcTestCommand
{
    public static readonly Command COMMAND = new("index-rpc-test") { Hidden = true };

    static IndexRpcTestCommand() =>
        COMMAND.SetAction(_ => new IndexRpcTestCommand().Handle());

    private IndexRpcTestCommand()
    {
    }

    private async Task<int> Handle()
    {
        const int    MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET = 1;
        const string GAME_ROOT_PATH                           = @"Z:\tgame\game";
        const string PATCH_ROOT_PATH                          = @"Z:\tgame";

        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(15));
        var cancellationToken       = cancellationTokenSource.Token;
        var launcher                = new Launcher();
        var areas                   = await LoginArea.Get();
        var area                    = areas[Random.Shared.Next(areas.Length)];
        var loginResult             = await launcher.UpdateClient.Check(area, new DirectoryInfo(PATCH_ROOT_PATH), true);

        if (loginResult.V3GameUpdatePlan == null)
            throw new InvalidDataException("Failed to get V3 update plan.");

        var updatePlan = loginResult.V3GameUpdatePlan;
        using var metadataClient = new V3PatchIndexMetadataClient();
        var latestVersions = await metadataClient.DownloadLatestVersionsAsync(cancellationToken);
        var gameVersionInfo = latestVersions[Repository.Ffxiv];
        if (!string.Equals(gameVersionInfo.Version, updatePlan.TargetGameVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"V3 target version mismatch: {updatePlan.TargetGameVersion} != {gameVersionInfo.Version}");

        var patchIndexFilePath = (await metadataClient.DownloadPatchIndexAsync
        (
            new DirectoryInfo(PATCH_ROOT_PATH),
            Repository.Ffxiv,
            gameVersionInfo.Version,
            gameVersionInfo.Revision,
            cancellationToken
        )).FullName;
        var availablePatchFiles = Directory.EnumerateFiles(PATCH_ROOT_PATH, "*.patch", SearchOption.AllDirectories)
                                           .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                                           .ToDictionary(static group => group.Key!, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        // Run verifier as another thread
        using var verifier = new IndexedZiPatchIndexRemoteInstaller(null, true);
        var patchIndex = new IndexedZiPatchIndex(new BinaryReader(new DeflateStream(new FileStream(patchIndexFilePath, FileMode.Open, FileAccess.Read), CompressionMode.Decompress)));

        await verifier.ConstructFromPatchFile(patchIndex, TimeSpan.FromSeconds(1));

        verifier.OnVerifyProgress  += ReportCheckProgress;
        verifier.OnInstallProgress += ReportInstallProgress;

        for (var attemptIndex = 0; attemptIndex < 5; attemptIndex++)
        {
            await verifier.SetTargetStreamsFromPathReadOnly(GAME_ROOT_PATH, cancellationToken);
            await verifier.VerifyFiles(attemptIndex > 0, Environment.ProcessorCount, cancellationToken);

            var missingPartIndicesPerTargetFile = await verifier.GetMissingPartIndicesPerTargetFile(cancellationToken);
            if (missingPartIndicesPerTargetFile.All(static parts => !parts.Any()))
                break;

            var missingPartIndicesPerPatch = await verifier.GetMissingPartIndicesPerPatch(cancellationToken);
            await verifier.SetTargetStreamsFromPathReadWriteForMissingFiles(GAME_ROOT_PATH, cancellationToken);

            for (var i = 0; i < patchIndex.Sources.Count; i++)
            {
                if (!missingPartIndicesPerPatch[i].Any())
                    continue;

                if (!availablePatchFiles.TryGetValue(patchIndex.Sources[i], out var patchFilePath))
                    throw new FileNotFoundException($"Missing local patch file: {patchIndex.Sources[i]}", patchIndex.Sources[i]);

                await verifier.QueueInstall(i, new FileInfo(patchFilePath));
            }

            await verifier.Install(MAX_CONCURRENT_CONNECTIONS_FOR_PATCH_SET, cancellationToken);
            await verifier.WriteVersionFiles(GAME_ROOT_PATH, cancellationToken);
        }

        verifier.OnVerifyProgress  -= ReportCheckProgress;
        verifier.OnInstallProgress -= ReportInstallProgress;

        return 0;

        void ReportCheckProgress(int index, long progress, long max) =>
            Log.Information
            (
                "[{0}/{1}] Checking file {2}... {3:0.00}/{4:0.00}MB ({5:00.00}%)",
                index + 1,
                patchIndex.Length,
                patchIndex[Math.Min(index, patchIndex.Length - 1)].RelativePath,
                progress         / 1048576.0,
                max              / 1048576.0,
                100.0 * progress / max
            );

        void ReportInstallProgress(int index, long progress, long max, IndexedZiPatchInstaller.InstallTaskState state) =>
            Log.Information
            (
                "[{0}/{1}] {2} {3}... {4:0.00}/{5:0.00}MB ({6:00.00}%)",
                index + 1,
                patchIndex.Sources.Count,
                state,
                patchIndex.Sources[Math.Min(index, patchIndex.Sources.Count - 1)],
                progress         / 1048576.0,
                max              / 1048576.0,
                100.0 * progress / max
            );
    }
}
