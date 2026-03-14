using System;

namespace XIVLauncher.Common.Game.Patch;

public class NotEnoughSpaceException : Exception
{
    public SpaceKind Kind { get; private set; }

    public long BytesRequired { get; set; }

    public long BytesFree { get; set; }

    public NotEnoughSpaceException(SpaceKind kind, long required, long free)
    {
        Kind          = kind;
        BytesRequired = required;
        BytesFree     = free;
    }

    public enum SpaceKind
    {
        Patches,
        AllPatches,
        Game
    }
}
