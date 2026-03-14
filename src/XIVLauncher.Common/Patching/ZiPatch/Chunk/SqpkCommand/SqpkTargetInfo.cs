using System.IO;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk.SqpkCommand;

internal class SqpkTargetInfo : SqpkChunk
{
    // Only Platform is used on recent patcher versions
    public new static string Command = "T";

    public ZiPatchConfig.PlatformId Platform        { get; protected set; }
    public RegionId                 Region          { get; protected set; }
    public bool                     IsDebug         { get; protected set; }
    public ushort                   Version         { get; protected set; }
    public ulong                    DeletedDataSize { get; protected set; }
    public ulong                    SeekCount       { get; protected set; }

    public SqpkTargetInfo(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override void ApplyChunk(ZiPatchConfig config) =>
        config.Platform = Platform;

    public override string ToString() =>
        $"{Type}:{Command}:{Platform}:{Region}:{IsDebug}:{Version}:{DeletedDataSize}:{SeekCount}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        // Reserved
        Reader.ReadBytes(3);

        Platform        = (ZiPatchConfig.PlatformId)Reader.ReadUInt16BE();
        Region          = (RegionId)Reader.ReadInt16BE();
        IsDebug         = Reader.ReadInt16BE() != 0;
        Version         = Reader.ReadUInt16BE();
        DeletedDataSize = Reader.ReadUInt64();
        SeekCount       = Reader.ReadUInt64();

        // Empty 32 + 64 bytes
    }

    // US/EU/JP are Global
    // ZH seems to also be Global
    // KR is unknown
    public enum RegionId : short
    {
        Global = -1
    }
}
