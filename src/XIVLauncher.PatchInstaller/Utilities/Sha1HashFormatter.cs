using System.Collections.Generic;
using System.Linq;

namespace XIVLauncher.PatchInstaller.Utilities;

internal static class Sha1HashFormatter
{
    public static string Format(IEnumerable<byte> hashBytes) =>
        string.Join(" ", hashBytes.Select(static value => value.ToString("X2")));
}
