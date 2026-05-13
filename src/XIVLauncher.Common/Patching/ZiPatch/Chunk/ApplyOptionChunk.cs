using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk;

public class ApplyOptionChunk : ZiPatchChunk
{
    public new static string Type = "APLY";

    // These are both false on all files seen
    public ApplyOptionKind OptionKind { get; protected set; }

    public bool OptionValue { get; protected set; }

    public ApplyOptionChunk(BinaryReader reader, long offset, long size)
        : base(reader, offset, size)
    {
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        switch (OptionKind)
        {
            case ApplyOptionKind.IgnoreMissing:
                config.IgnoreMissing = OptionValue;
                break;

            case ApplyOptionKind.IgnoreOldMismatch:
                config.IgnoreOldMismatch = OptionValue;
                break;
        }
    }

    public override string ToString() =>
        $"{Type}:{OptionKind}:{OptionValue}";

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        OptionKind = (ApplyOptionKind)Reader.ReadUInt32BE();

        // Discarded padding, always 0x0000_0004 as far as observed
        Reader.ReadBytes(4);

        var value = Reader.ReadUInt32BE() != 0;

        if (OptionKind == ApplyOptionKind.IgnoreMissing || OptionKind == ApplyOptionKind.IgnoreOldMismatch)
            OptionValue = value;
        else
            OptionValue = false; // defaults to false if OptionKind isn't valid
    }

    public enum ApplyOptionKind : uint
    {
        IgnoreMissing     = 1,
        IgnoreOldMismatch = 2
    }
}
