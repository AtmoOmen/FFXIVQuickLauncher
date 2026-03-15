using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Patching.ZiPatch;

namespace XIVLauncher.Common.Patching.IndexedZiPatch;

public class IndexedZiPatchOperations
{
    public static async Task<IndexedZiPatchIndex> CreateZiPatchIndices
    (
        int               expacVersion,
        IList<string>     patchFilePaths,
        CancellationToken cancellationToken = default
    )
    {
        var sources    = new List<Stream>();
        var patchFiles = new List<ZiPatchFile>();
        var patchIndex = new IndexedZiPatchIndex(expacVersion);

        try
        {
            var firstPatchFileIndex = patchFilePaths.Count - 1;

            while (firstPatchFileIndex > 0)
            {
                if (File.Exists(patchFilePaths[firstPatchFileIndex] + ".index"))
                    break;

                firstPatchFileIndex--;
            }

            for (var i = 0; i < patchFilePaths.Count; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var patchFilePath = patchFilePaths[i];
                sources.Add(new FileStream(patchFilePath, FileMode.Open, FileAccess.Read));
                patchFiles.Add(new(sources[^1]));

                if (i < firstPatchFileIndex)
                    continue;

                if (File.Exists(patchFilePath + ".index"))
                {
                    Log.Information("Reading patch index file {0}...", patchFilePath);
                    patchIndex = new(new BinaryReader(new DeflateStream(new FileStream(patchFilePath + ".index", FileMode.Open, FileAccess.Read), CompressionMode.Decompress)));
                    continue;
                }

                Log.Information("Indexing patch file {0}...", patchFilePath);
                await patchIndex.ApplyZiPatch(Path.GetFileName(patchFilePath), patchFiles[^1], cancellationToken);

                Log.Information("Calculating CRC32 for files resulted from patch file {0}...", patchFilePath);
                await patchIndex.CalculateCrc32(sources, cancellationToken);

                using (var writer = new BinaryWriter(new DeflateStream(new FileStream(patchFilePath + ".index.tmp", FileMode.Create), CompressionLevel.Optimal)))
                    patchIndex.WriteTo(writer);

                File.Move(patchFilePath + ".index.tmp", patchFilePath + ".index");
            }

            return patchIndex;
        }
        finally
        {
            foreach (var source in sources)
                source.Dispose();
        }
    }

    public static async Task<IndexedZiPatchInstaller> VerifyFromZiPatchIndex
    (
        string            patchIndexFilePath,
        string            gameRootPath,
        int               concurrentCount,
        CancellationToken cancellationToken = default
    )
    {
        return await VerifyFromZiPatchIndex
               (
                   new IndexedZiPatchIndex(new BinaryReader(new DeflateStream(new FileStream(patchIndexFilePath, FileMode.Open, FileAccess.Read), CompressionMode.Decompress))),
                   gameRootPath,
                   concurrentCount,
                   cancellationToken
               );
    }

    public static async Task<IndexedZiPatchInstaller> VerifyFromZiPatchIndex
    (
        IndexedZiPatchIndex patchIndex,
        string              gameRootPath,
        int                 concurrentCount,
        CancellationToken   cancellationToken = default
    )
    {
        var verifier = new IndexedZiPatchInstaller(patchIndex)
        {
            ProgressReportInterval = 1000
        };

        var remainingErrorMessagesToShow = 8;

        void OnCorruptionFoundCallback(IndexedZiPatchPartLocator part, IndexedZiPatchPartLocator.VerifyDataResult result)
        {
            switch (result)
            {
                case IndexedZiPatchPartLocator.VerifyDataResult.FailNotEnoughData:
                    if (remainingErrorMessagesToShow > 0)
                    {
                        Log.Error("{0}:{1}:{2}: Premature EOF detected", patchIndex[part.TargetIndex].RelativePath, part.TargetOffset, patchIndex[part.TargetIndex].FileSize);
                        remainingErrorMessagesToShow = 0;
                    }

                    break;

                case IndexedZiPatchPartLocator.VerifyDataResult.FailBadData:
                    if (remainingErrorMessagesToShow > 0)
                    {
                        Log.Warning
                        (
                            --remainingErrorMessagesToShow == 0 ? "{0}:{1}:{2}: Corrupt data; suppressing further corruption warnings for this file." : "{0}:{1}:{2}: Corrupt data",
                            patchIndex[part.TargetIndex].RelativePath,
                            part.TargetOffset,
                            part.TargetEnd
                        );
                    }

                    break;
            }
        }

        verifier.OnVerifyProgress  += OnVerifyProgressCallback;
        verifier.OnCorruptionFound += OnCorruptionFoundCallback;

        try
        {
            verifier.SetTargetStreamsFromPathReadOnly(gameRootPath);
            await verifier.VerifyFiles(false, concurrentCount, cancellationToken);
        }
        finally
        {
            verifier.OnVerifyProgress  -= OnVerifyProgressCallback;
            verifier.OnCorruptionFound -= OnCorruptionFoundCallback;
        }

        return verifier;

        void OnVerifyProgressCallback(int index, long progress, long max) => Log.Information
        (
            "[{0}/{1}] Checking file {2}... {3:0.00}/{4:0.00}MB ({5:00.00}%)",
            index + 1,
            patchIndex.Length,
            patchIndex[Math.Min(index, patchIndex.Length - 1)].RelativePath,
            progress         / 1048576.0,
            max              / 1048576.0,
            100.0 * progress / max
        );
    }

    public static async Task RepairFromPatchFileIndexFromFile
    (
        IndexedZiPatchIndex patchIndex,
        string              gameRootPath,
        string              patchFileRootDir,
        int                 concurrentCount,
        CancellationToken   cancellationToken = default
    )
    {
        using var verifier = await VerifyFromZiPatchIndex(patchIndex, gameRootPath, concurrentCount, cancellationToken);
        verifier.SetTargetStreamsFromPathReadWriteForMissingFiles(gameRootPath);
        for (var i = 0; i < patchIndex.Sources.Count; i++)
            verifier.QueueInstall(i, new(Path.Combine(patchFileRootDir, patchIndex.Sources[i])));
        await verifier.Install(concurrentCount, cancellationToken);
    }

    public static async Task RepairFromPatchFileIndexFromFile
    (
        string            patchIndexFilePath,
        string            gameRootPath,
        string            patchFileRootDir,
        int               concurrentCount,
        CancellationToken cancellationToken = default
    ) =>
        await RepairFromPatchFileIndexFromFile
        (
            new IndexedZiPatchIndex(new BinaryReader(new DeflateStream(new FileStream(patchIndexFilePath, FileMode.Open, FileAccess.Read), CompressionMode.Decompress))),
            gameRootPath,
            patchFileRootDir,
            concurrentCount,
            cancellationToken
        );
}
