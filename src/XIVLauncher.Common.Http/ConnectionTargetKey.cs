using System.Net;

namespace XIVLauncher.Common.Http;

internal readonly record struct ConnectionTargetKey
(
    string    Host,
    IPAddress Address
);
