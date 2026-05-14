namespace XIVLauncher.Account.Cred;

public sealed record CredTypeApplyResult
(
    bool     Succeeded,
    CredType RequestedCredType,
    CredType AppliedCredType,
    bool     WasFallbackApplied         = false,
    bool     HasUnavailableSavedSecrets = false,
    string?  UserMessage                = null
);
