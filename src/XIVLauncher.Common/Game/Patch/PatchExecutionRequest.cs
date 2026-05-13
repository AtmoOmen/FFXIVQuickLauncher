namespace XIVLauncher.Common.Game.Patch;

public sealed class PatchExecutionRequest
{
    public required string       MutexName             { get; init; }
    public required PatchManager Patcher               { get; init; }
    public required FileInfo     AriaLogFile           { get; init; }
    public required Func<bool>   IsGameOpen            { get; init; }
    public required Func<bool>   ContinueWhenGameOpen  { get; init; }
    public required Func<bool>   EnsureGameFilesClosed { get; init; }
}
