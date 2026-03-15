#nullable enable

using System;
using System.Text.RegularExpressions;
using Serilog;

namespace XIVLauncher.Common.Game.Exceptions;

[Serializable]
public partial class OAuthLoginException
(
    string document
) : Exception(document ?? "未知错误")
{
    public string? OauthErrorMessage { get; private set; } = document;

    private static string? GetErrorMessage(string document)
    {
        var matches = ErrorMessageRegex().Matches(document);

        if (matches.Count is 0 or > 1)
        {
            Log.Error("Could not get login error\n{Doc}", document);
            return null;
        }

        return matches[0].Groups["errorMessage"].Value;
    }

    [GeneratedRegex
    (
        """window.external.user\("login=auth,ng,err,(?<errorMessage>.*)\"\);""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    )]
    private static partial Regex ErrorMessageRegex();
}
