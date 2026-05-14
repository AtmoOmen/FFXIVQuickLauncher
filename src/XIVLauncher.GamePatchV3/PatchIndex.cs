using System.Text.RegularExpressions;

namespace XIVLauncher.GamePatchV3;

public class PatchIndex
{
    public const uint FileSignature = 0x89AA3CD1;
    public const uint FileVersion   = 2;

    public const int ExpacVersionBoot     = -1;
    public const int ExpacVersionBaseGame = 0;

    public readonly int ExpacVersion;

    public IList<string>                   Sources => sourceFiles.AsReadOnly();
    public IList<PatchTargetFile> Targets => targetFiles.AsReadOnly();

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

    public PatchTargetFile this[int    index] => targetFiles[index];
    public PatchTargetFile this[string name] => targetFiles[IndexOf(name)];
    public int    Length          => targetFiles.Count;
    public string VersionName     => versionNameRegex.Match(sourceFiles.Last()).Groups["version"].Value;
    public string VersionFileBase => ExpacVersion == ExpacVersionBoot ? "ffxivboot" : ExpacVersion == ExpacVersionBaseGame ? "ffxivgame" : $"sqpack/ex{ExpacVersion}/ex{ExpacVersion}";
    public string VersionFileVer  => VersionFileBase + ".ver";
    public string VersionFileBck  => VersionFileBase + ".bck";

    private readonly List<string>                   sourceFiles          = [];
    private readonly List<long>                     sourceFileLastPtr    = [];
    private readonly List<PatchTargetFile> targetFiles          = [];
    private readonly List<IList<Tuple<int, int>>>   sourceFilePartsCache = [];
    private readonly Regex                          versionNameRegex     = new(@"[A-Z](?<version>[0-9.]+)[a-z]*\.patch");

    public PatchIndex(int expacVersion) =>
        ExpacVersion = expacVersion;

    public PatchIndex(BinaryReader reader, bool disposeReader = true)
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

    private Tuple<int, PatchTargetFile> AllocFile(string target)
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
