using System;

namespace XIVLauncher.Common.Game.Exceptions;

public class DCTravelAPIException : Exception
{
    public bool IsNetworkTimeout { get; set; }
    public bool RetryAfterOneMin   { get; set; }

    public DCTravelAPIException(string message, int errorCode = 0)
        : base(message)
    {
        switch (errorCode)
        {
            // 网络超时，请稍后重试！
            case -10339000:
                IsNetworkTimeout = true;
                break;

            // 请求过于频繁，请稍等1分钟后再试。
            case -10339325:
                RetryAfterOneMin = true;
                break;
        }

    }
}
