using System.IO;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk;

public class AddDirectoryChunk : ZiPatchChunk
{
    public new static string Type = "ADIR";

    public string DirName { get; protected set; }

    public AddDirectoryChunk(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override void ApplyChunk(ZiPatchConfig config) =>
        Directory.CreateDirectory(config.GamePath + DirName);

    public override string ToString() =>
        $"{Type}:{DirName}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        var       dirNameLen   = Reader.ReadUInt32BE();

        DirName = Reader.ReadFixedLengthString(dirNameLen);
    }
}
