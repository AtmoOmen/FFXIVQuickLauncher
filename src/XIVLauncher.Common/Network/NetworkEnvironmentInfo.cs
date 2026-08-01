namespace XIVLauncher.Common.Network;

public sealed record NetworkEnvironmentInfo
(
    NetworkRegion  Region,
    string?        CountryCode,
    DateTimeOffset DetectedAtUTC
);
