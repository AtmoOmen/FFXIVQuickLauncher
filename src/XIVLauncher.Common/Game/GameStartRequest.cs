namespace XIVLauncher.Common.Game;

public sealed record GameStartRequest
(
    string                      ExePath,
    string                      WorkingDirectory,
    string                      Arguments,
    IDictionary<string, string> Environment,
    DPIAwareness                DpiAwareness
);
