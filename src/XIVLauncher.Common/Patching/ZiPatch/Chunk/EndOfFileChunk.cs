using System.IO;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk;

public class EndOfFileChunk : ZiPatchChunk
{
    public new static string Type = "EOF_";

    public EndOfFileChunk(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override string ToString() =>
        Type;

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
    }
}
