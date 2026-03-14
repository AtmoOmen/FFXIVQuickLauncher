using System.IO;
using XIVLauncher.Common.Patching.Util;
using XIVLauncher.Common.Patching.ZiPatch.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk.SqpkCommand;

internal class SqpkAddData : SqpkChunk
{
    public new static string Command = "A";

    public SqpackDatFile TargetFile        { get; protected set; }
    public long          BlockOffset       { get; protected set; }
    public long          BlockNumber       { get; protected set; }
    public long          BlockDeleteNumber { get; protected set; }

    public byte[] BlockData             { get; protected set; }
    public long   BlockDataSourceOffset { get; protected set; }

    public SqpkAddData(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        TargetFile.ResolvePath(config.Platform);

        var file = config.Store == null ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate) : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

        file.WriteFromOffset(BlockData, BlockOffset);
        file.Wipe(BlockDeleteNumber);
    }

    public override string ToString() =>
        $"{Type}:{Command}:{TargetFile}:{BlockOffset}:{BlockNumber}:{BlockDeleteNumber}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Reader.ReadBytes(3); // Alignment

        TargetFile = new SqpackDatFile(Reader);

        BlockOffset       = (long)Reader.ReadUInt32BE() << 7;
        BlockNumber       = (long)Reader.ReadUInt32BE() << 7;
        BlockDeleteNumber = (long)Reader.ReadUInt32BE() << 7;

        BlockDataSourceOffset = Offset + Reader.BaseStream.Position;
        BlockData             = Reader.ReadBytes(checked((int)BlockNumber));
    }
}
