using System.IO;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk;

public class ApplyFreeSpaceChunk : ZiPatchChunk
{
    // This is a NOP on recent patcher versions, so I don't think we'll be seeing it.
    public new static string Type = "APFS";

    // TODO: No samples of this were found, so these fields are theoretical
    public long UnknownFieldA { get; protected set; }
    public long UnknownFieldB { get; protected set; }

    public ApplyFreeSpaceChunk(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override string ToString() =>
        $"{Type}:{UnknownFieldA}:{UnknownFieldB}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        UnknownFieldA = Reader.ReadInt64BE();
        UnknownFieldB = Reader.ReadInt64BE();
    }
}
