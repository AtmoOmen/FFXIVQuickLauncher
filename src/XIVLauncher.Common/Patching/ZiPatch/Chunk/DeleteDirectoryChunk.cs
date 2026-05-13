using Serilog;
using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk;

public class DeleteDirectoryChunk : ZiPatchChunk
{
    public new static string Type = "DELD";

    public string DirName { get; protected set; }

    public DeleteDirectoryChunk(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        try
        {
            Directory.Delete(config.GamePath + DirName);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Ran into {This}, failed at deleting the dir", this);
            throw;
        }
    }

    public override string ToString() =>
        $"{Type}:{DirName}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        var       dirNameLen   = Reader.ReadUInt32BE();

        DirName = Reader.ReadFixedLengthString(dirNameLen);
    }
}
