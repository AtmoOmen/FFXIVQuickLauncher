namespace XIVLauncher.GamePatchV3.Integrity.Models;

public sealed class IntegrityCheckCompareOutcome
{
    public required IntegrityCheckCompareResult CompareResult { get; init; }

    public string Report { get; init; } = string.Empty;

    public IntegrityCheckResult? RemoteIntegrity { get; init; }
}
