using System.Net;

namespace XIVLauncher.Common.Http;

internal readonly record struct ConnectionCandidate
(
    IPAddress                 Address,
    ConnectionCandidateSource Source,
    int                       OriginalIndex
);
