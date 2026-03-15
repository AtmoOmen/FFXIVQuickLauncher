using System;

namespace XIVLauncher.Common.Game.Patch;

public sealed class PatchExecutionResult
{
    public required PatchExecutionStatus Status    { get; init; }
    public          Exception?           Exception { get; init; }
}
