using System.IO;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk;

// ReSharper disable once InconsistentNaming
public class XXXXChunk : ZiPatchChunk
{
    // TODO: This... Never happens.
    public new static string Type = "XXXX";

    public XXXXChunk(BinaryReader reader, long offset, long size)
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
