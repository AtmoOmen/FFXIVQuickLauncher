namespace XIVLauncher.Support.Velopack;

internal sealed record VelopackRestartState
(
    string ExecutableRelativePath,
    string[] Arguments
);
