namespace XIVLauncher.DCTravel;

public class DCTravelAPIException : Exception
{
    public bool IsNetworkTimeout { get; set; }
    public bool RetryAfterOneMin { get; set; }

    public DCTravelAPIException(string message, int errorCode = 0)
        : base(message)
    {
        switch (errorCode)
        {
            case -10339000:
                IsNetworkTimeout = true;
                break;

            case -10339325:
                RetryAfterOneMin = true;
                break;
        }
    }
}
