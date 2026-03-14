using System.IO;
using XIVLauncher.Common.Patching.Util;
using XIVLauncher.Common.Patching.ZiPatch.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk.SqpkCommand;

internal class SqpkIndex : SqpkChunk
{
    // This is a NOP on recent patcher versions.
    public new static string Command = "I";

    public IndexCommandKind IndexCommand { get; protected set; }
    public bool             IsSynonym    { get; protected set; }
    public SqpackIndexFile  TargetFile   { get; protected set; }
    public ulong            FileHash     { get; protected set; }
    public uint             BlockOffset  { get; protected set; }

    // TODO: Figure out what this is used for
    public uint BlockNumber { get; protected set; }

    public SqpkIndex(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override string ToString() =>
        $"{Type}:{Command}:{IndexCommand}:{IsSynonym}:{TargetFile}:{FileHash:X8}:{BlockOffset}:{BlockNumber}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        IndexCommand = (IndexCommandKind)Reader.ReadByte();
        IsSynonym    = Reader.ReadBoolean();
        Reader.ReadByte(); // Alignment

        TargetFile = new SqpackIndexFile(Reader);

        FileHash = Reader.ReadUInt64BE();

        BlockOffset = Reader.ReadUInt32BE();
        BlockNumber = Reader.ReadUInt32BE();
    }

    public enum IndexCommandKind : byte
    {
        Add    = (byte)'A',
        Delete = (byte)'D'
    }
}
