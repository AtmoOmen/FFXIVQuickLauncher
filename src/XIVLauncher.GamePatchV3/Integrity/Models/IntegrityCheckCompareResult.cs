namespace XIVLauncher.GamePatchV3.Integrity.Models;

public enum IntegrityCheckCompareResult
{
    Valid,
    Invalid,
    VersionUnsupported,
    ReferenceNotFound,
    ReferenceFetchFailure
}
