namespace XIVLauncher.Accounts.Cred;

public sealed record CredTypeApplyResult
(
    bool      Succeeded,
    CredType  RequestedCredType,
    CredType  AppliedCredType,
    bool      WasFallbackApplied          = false,
    bool      ShouldDisableAutoLogin      = false,
    bool      HasUnavailableSavedSecrets  = false,
    string?   UserMessage                 = null
);
