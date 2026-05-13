using System.Text.RegularExpressions;
using XIVLauncher.Common.Patching.ZiPatch;
using XIVLauncher.Common.Patching.ZiPatch.Chunk;
using XIVLauncher.Common.Patching.ZiPatch.Chunk.SqpkCommand;
using XIVLauncher.Common.Patching.ZiPatch.Util;

namespace XIVLauncher.Common.Patching.IndexedZiPatch;

public class IndexedZiPatchIndex
{
    public const uint FileSignature = 0x89AA3CD1;
    public const uint FileVersion   = 2;

    public const int ExpacVersionBoot     = -1;
    public const int ExpacVersionBaseGame = 0;

    public readonly int ExpacVersion;

    public IList<string>                   Sources => sourceFiles.AsReadOnly();
    public IList<IndexedZiPatchTargetFile> Targets => targetFiles.AsReadOnly();

    public IList<IList<Tuple<int, int>>> SourceParts
    {
        get
        {
            for (var sourceFileIndex = sourceFilePartsCache.Count; sourceFileIndex < sourceFiles.Count; sourceFileIndex++)
            {
                var list = new List<Tuple<int, int>>();

                for (var i = 0; i < targetFiles.Count; i++)
                {
                    for (var j = 0; j < targetFiles[i].Count; j++)
                    {
                        if (targetFiles[i][j].SourceIndex == sourceFileIndex)
                            list.Add(Tuple.Create(i, j));
                    }
                }

                list.Sort((x, y) => targetFiles[x.Item1][x.Item2].SourceOffset.CompareTo(targetFiles[y.Item1][y.Item2].SourceOffset));
                sourceFilePartsCache.Add(list.AsReadOnly());
            }

            return sourceFilePartsCache.AsReadOnly();
        }
    }

    public IndexedZiPatchTargetFile this[int    index] => targetFiles[index];
    public IndexedZiPatchTargetFile this[string name] => targetFiles[IndexOf(name)];
    public int    Length          => targetFiles.Count;
    public string VersionName     => versionNameRegex.Match(sourceFiles.Last()).Groups["version"].Value;
    public string VersionFileBase => ExpacVersion == ExpacVersionBoot ? "ffxivboot" : ExpacVersion == ExpacVersionBaseGame ? "ffxivgame" : $"sqpack/ex{ExpacVersion}/ex{ExpacVersion}";
    public string VersionFileVer  => VersionFileBase + ".ver";
    public string VersionFileBck  => VersionFileBase + ".bck";

    private readonly List<string>                   sourceFiles          = [];
    private readonly List<long>                     sourceFileLastPtr    = [];
    private readonly List<IndexedZiPatchTargetFile> targetFiles          = [];
    private readonly List<IList<Tuple<int, int>>>   sourceFilePartsCache = [];
    private readonly Regex                          versionNameRegex     = new(@"[A-Z](?<version>[0-9.]+)[a-z]*\.patch");

    public IndexedZiPatchIndex(int expacVersion) =>
        ExpacVersion = expacVersion;

    public IndexedZiPatchIndex(BinaryReader reader, bool disposeReader = true)
    {
        try
        {
            if (reader.ReadUInt32() != FileSignature)
                throw new InvalidDataException("Not a valid ZiPatch index file.");
            if (reader.ReadUInt32() != FileVersion)
                throw new InvalidDataException("Not a valid ZiPatch index file version.");

            ExpacVersion = reader.ReadInt32();

            for (int i = 0, readIndex = reader.ReadInt32(); i < readIndex; i++)
                sourceFiles.Add(reader.ReadString());
            foreach (var _ in sourceFiles)
                sourceFileLastPtr.Add(reader.ReadInt64());

            for (int i = 0, readIndex = reader.ReadInt32(); i < readIndex; i++)
                targetFiles.Add(new(reader, false));
        }
        finally
        {
            if (disposeReader)
                reader.Dispose();
        }
    }

    public long GetSourceLastPtr(int index) => sourceFileLastPtr[index];

    public int IndexOf(string name) => targetFiles.FindIndex(x => x.RelativePath == NormalizePath(name));

    public async Task ApplyZiPatch(string patchFileName, ZiPatchFile patchFile, CancellationToken cancellationToken = default)
    {
        await Task.Run
        (
            () =>
            {
                var sourceIndex = sourceFiles.Count;
                sourceFiles.Add(patchFileName);
                sourceFileLastPtr.Add(0);
                sourceFilePartsCache.Clear();

                var platform = ZiPatchConfig.PlatformId.Win32;

                foreach (var patchChunk in patchFile.GetChunks())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    switch (patchChunk)
                    {
                        case DeleteDirectoryChunk deleteDirectoryChunk:
                        {
                            var prefix = NormalizePath(deleteDirectoryChunk.DirName.ToLowerInvariant());
                            targetFiles.RemoveAll(x => x.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                            ReassignTargetIndices();
                            break;
                        }

                        case SqpkTargetInfo sqpkTargetInfo:
                            platform = sqpkTargetInfo.Platform;
                            break;

                        case SqpkFile sqpkFile:
                            switch (sqpkFile.Operation)
                            {
                                case SqpkFile.OperationKind.AddFile:
                                    var (targetIndex, file) = AllocFile(sqpkFile.TargetFile.RelativePath);
                                    if (sqpkFile.FileOffset == 0)
                                        file.Clear();

                                    var offset = sqpkFile.FileOffset;

                                    for (var i = 0; i < sqpkFile.CompressedData.Count; ++i)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();

                                        var block      = sqpkFile.CompressedData[i];
                                        var dataOffset = sqpkFile.CompressedDataSourceOffsets[i];

                                        if (block.IsCompressed)
                                        {
                                            file.Update
                                            (
                                                new()
                                                {
                                                    TargetOffset        = offset,
                                                    TargetSize          = block.DecompressedSize,
                                                    TargetIndex         = targetIndex,
                                                    SourceIndex         = sourceIndex,
                                                    SourceOffset        = dataOffset,
                                                    IsDeflatedBlockData = true
                                                }
                                            );
                                            sourceFileLastPtr[sourceFileLastPtr.Count - 1] = dataOffset + block.CompressedSize;
                                        }
                                        else
                                        {
                                            file.Update
                                            (
                                                new()
                                                {
                                                    TargetOffset = offset,
                                                    TargetSize   = block.DecompressedSize,
                                                    TargetIndex  = targetIndex,
                                                    SourceIndex  = sourceIndex,
                                                    SourceOffset = dataOffset
                                                }
                                            );
                                            sourceFileLastPtr[sourceFileLastPtr.Count - 1] = dataOffset + block.DecompressedSize;
                                        }

                                        offset += block.DecompressedSize;
                                    }

                                    break;

                                case SqpkFile.OperationKind.RemoveAll:
                                    var xpacPath = SqexFile.GetExpansionFolder((byte)sqpkFile.ExpansionId);

                                    targetFiles.RemoveAll(x => x.RelativePath.StartsWith($"sqpack/{xpacPath}", StringComparison.OrdinalIgnoreCase));
                                    targetFiles.RemoveAll(x => x.RelativePath.StartsWith($"movie/{xpacPath}",  StringComparison.OrdinalIgnoreCase));
                                    ReassignTargetIndices();
                                    break;

                                case SqpkFile.OperationKind.DeleteFile:
                                    targetFiles.RemoveAll(x => x.RelativePath.ToLowerInvariant() == sqpkFile.TargetFile.RelativePath.ToLowerInvariant());
                                    ReassignTargetIndices();
                                    break;
                            }

                            break;

                        case SqpkAddData sqpkAddData:
                        {
                            sqpkAddData.TargetFile.ResolvePath(platform);
                            var (targetIndex, file) = AllocFile(sqpkAddData.TargetFile.RelativePath);
                            file.Update
                            (
                                new()
                                {
                                    TargetOffset                     = sqpkAddData.BlockOffset,
                                    TargetSize                       = sqpkAddData.BlockNumber,
                                    TargetIndex                      = targetIndex,
                                    SourceIndex                      = sourceIndex,
                                    SourceOffset                     = sqpkAddData.BlockDataSourceOffset,
                                    Crc32OrPlaceholderEntryDataUnits = (uint)(sqpkAddData.BlockNumber >> 7) - 1
                                }
                            );
                            sourceFileLastPtr[sourceFileLastPtr.Count - 1] = (int)(sqpkAddData.BlockDataSourceOffset + sqpkAddData.BlockNumber);
                            file.Update
                            (
                                new()
                                {
                                    TargetOffset                     = sqpkAddData.BlockOffset + sqpkAddData.BlockNumber,
                                    TargetSize                       = sqpkAddData.BlockDeleteNumber,
                                    TargetIndex                      = targetIndex,
                                    SourceIndex                      = IndexedZiPatchPartLocator.SourceIndexZeros,
                                    Crc32OrPlaceholderEntryDataUnits = (uint)(sqpkAddData.BlockDeleteNumber >> 7) - 1
                                }
                            );
                            break;
                        }

                        case SqpkDeleteData sqpkDeleteData:
                        {
                            sqpkDeleteData.TargetFile.ResolvePath(platform);
                            var (targetIndex, file) = AllocFile(sqpkDeleteData.TargetFile.RelativePath);

                            if (sqpkDeleteData.BlockNumber > 0)
                            {
                                file.Update
                                (
                                    new()
                                    {
                                        TargetOffset                     = sqpkDeleteData.BlockOffset,
                                        TargetSize                       = 1 << 7,
                                        TargetIndex                      = targetIndex,
                                        SourceIndex                      = IndexedZiPatchPartLocator.SourceIndexEmptyBlock,
                                        Crc32OrPlaceholderEntryDataUnits = (uint)sqpkDeleteData.BlockNumber - 1
                                    }
                                );

                                if (sqpkDeleteData.BlockNumber > 1)
                                {
                                    file.Update
                                    (
                                        new()
                                        {
                                            TargetOffset = sqpkDeleteData.BlockOffset + (1 << 7),
                                            TargetSize   = sqpkDeleteData.BlockNumber - 1 << 7,
                                            TargetIndex  = targetIndex,
                                            SourceIndex  = IndexedZiPatchPartLocator.SourceIndexZeros
                                        }
                                    );
                                }
                            }

                            break;
                        }

                        case SqpkExpandData sqpkExpandData:
                        {
                            sqpkExpandData.TargetFile.ResolvePath(platform);
                            var (targetIndex, file) = AllocFile(sqpkExpandData.TargetFile.RelativePath);

                            if (sqpkExpandData.BlockNumber > 0)
                            {
                                file.Update
                                (
                                    new()
                                    {
                                        TargetOffset                     = sqpkExpandData.BlockOffset,
                                        TargetSize                       = 1 << 7,
                                        TargetIndex                      = targetIndex,
                                        SourceIndex                      = IndexedZiPatchPartLocator.SourceIndexEmptyBlock,
                                        Crc32OrPlaceholderEntryDataUnits = (uint)sqpkExpandData.BlockNumber - 1
                                    }
                                );

                                if (sqpkExpandData.BlockNumber > 1)
                                {
                                    file.Update
                                    (
                                        new()
                                        {
                                            TargetOffset = sqpkExpandData.BlockOffset + (1 << 7),
                                            TargetSize   = sqpkExpandData.BlockNumber - 1 << 7,
                                            TargetIndex  = targetIndex,
                                            SourceIndex  = IndexedZiPatchPartLocator.SourceIndexZeros
                                        }
                                    );
                                }
                            }

                            break;
                        }

                        case SqpkHeader sqpkHeader:
                        {
                            sqpkHeader.TargetFile.ResolvePath(platform);
                            var (targetIndex, file) = AllocFile(sqpkHeader.TargetFile.RelativePath);
                            file.Update
                            (
                                new()
                                {
                                    TargetOffset = sqpkHeader.HeaderKind == SqpkHeader.TargetHeaderKind.Version ? 0 : SqpkHeader.HEADER_SIZE,
                                    TargetSize   = SqpkHeader.HEADER_SIZE,
                                    TargetIndex  = targetIndex,
                                    SourceIndex  = sourceIndex,
                                    SourceOffset = sqpkHeader.HeaderDataSourceOffset
                                }
                            );
                            sourceFileLastPtr[sourceFileLastPtr.Count - 1] = (int)(sqpkHeader.HeaderDataSourceOffset + SqpkHeader.HEADER_SIZE);
                            break;
                        }
                    }
                }
            },
            cancellationToken
        );
    }

    public async Task CalculateCrc32(List<Stream> sources, CancellationToken cancellationToken = default)
    {
        foreach (var file in targetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await file.CalculateCrc32(sources, cancellationToken);
        }
    }

    public void WriteTo(BinaryWriter writer)
    {
        writer.Write(FileSignature);
        writer.Write(FileVersion);
        writer.Write(ExpacVersion);

        writer.Write(sourceFiles.Count);
        foreach (var file in sourceFiles)
            writer.Write(file);
        foreach (var file in sourceFileLastPtr)
            writer.Write(file);

        writer.Write(targetFiles.Count);
        foreach (var file in targetFiles)
            file.WriteTo(writer);
    }

    private static string NormalizePath(string path)
    {
        if (path == "")
            return path;

        path = path.Replace("\\", "/");
        while (path[0] == '/')
            path = path.Substring(1);
        return path;
    }

    private void ReassignTargetIndices()
    {
        for (var i = 0; i < targetFiles.Count; i++)
        {
            for (var j = 0; j < targetFiles[i].Count; j++)
            {
                var obj = targetFiles[i][j];
                obj.TargetIndex   = i;
                targetFiles[i][j] = obj;
            }
        }
    }

    private Tuple<int, IndexedZiPatchTargetFile> AllocFile(string target)
    {
        target = NormalizePath(target);
        var targetFileIndex = IndexOf(target);

        if (targetFileIndex == -1)
        {
            targetFiles.Add(new(target));
            targetFileIndex = targetFiles.Count - 1;
        }

        return Tuple.Create(targetFileIndex, targetFiles[targetFileIndex]);
    }
}
