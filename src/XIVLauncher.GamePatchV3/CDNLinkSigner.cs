using System.Security.Cryptography;
using System.Text;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.GamePatchV3;

public static class CDNLinkSigner
{
    public static Uri Sign(Uri uri)
    {
        var timeStampHex = DateTimeOffset.Now.ToUnixTimeSeconds().ToString("x");
        var hashBytes    = MD5.HashData(Encoding.UTF8.GetBytes($"{SdoInfos.CDN_KEY}{uri.AbsolutePath}{timeStampHex}"));
        var cdnKey       = Convert.ToHexStringLower(hashBytes);

        return new($"{uri.Scheme}://{uri.Host}/{cdnKey}/{timeStampHex}{uri.AbsolutePath}");
    }
}
