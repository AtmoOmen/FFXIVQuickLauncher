using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk.SqpkCommand;

internal class SqpkPatchInfo : SqpkChunk
{
    // This is a NOP on recent patcher versions
    public new static string Command = "X";

    // Don't know what this stuff is for
    public byte  Status      { get; protected set; }
    public byte  Version     { get; protected set; }
    public ulong InstallSize { get; protected set; }

    public SqpkPatchInfo(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override string ToString() =>
        $"{Type}:{Command}:{Status}:{Version}:{InstallSize}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Status  = Reader.ReadByte();
        Version = Reader.ReadByte();
        Reader.ReadByte(); // Alignment

        InstallSize = Reader.ReadUInt64BE();
    }
}
