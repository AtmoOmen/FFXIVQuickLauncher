using System.IO;
using XIVLauncher.Common.Patching.Util;
using XIVLauncher.Common.Patching.ZiPatch.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk.SqpkCommand;

internal class SqpkDeleteData : SqpkChunk
{
    public new static string Command = "D";

    public SqpackDatFile TargetFile  { get; protected set; }
    public long          BlockOffset { get; protected set; }
    public long          BlockNumber { get; protected set; }

    public SqpkDeleteData(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        TargetFile.ResolvePath(config.Platform);

        var file = config.Store == null ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate) : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

        SqpackDatFile.WriteEmptyFileBlockAt(file, BlockOffset, BlockNumber);
    }

    public override string ToString() =>
        $"{Type}:{Command}:{TargetFile}:{BlockOffset}:{BlockNumber}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Reader.ReadBytes(3); // Alignment

        TargetFile = new SqpackDatFile(Reader);

        BlockOffset = (long)Reader.ReadUInt32BE() << 7;
        BlockNumber = Reader.ReadUInt32BE();

        Reader.ReadUInt32(); // Reserved
    }
}
