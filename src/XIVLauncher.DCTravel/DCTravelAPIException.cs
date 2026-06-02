namespace XIVLauncher.DCTravel;

public class DCTravelAPIException : Exception
{
    public bool    IsNetworkTimeout     { get; set; }
    public bool    RetryAfterOneMin     { get; set; }
    public bool    IsEnvelopeRejected   { get; set; }
    public bool    IsServiceMaintenance { get; set; }
    public int     ErrorCode            { get; }
    public string? ReturnMessage        { get; }

    public DCTravelAPIException(string message, int errorCode = 0, string? returnMessage = null)
        : base(message)
    {
        ErrorCode      = errorCode;
        ReturnMessage  = returnMessage;

        switch (errorCode)
        {
            case -10339000:
                IsNetworkTimeout = true;
                break;

            case -10339325:
                RetryAfterOneMin = true;
                break;

            case -10339180:
                IsServiceMaintenance = true;
                break;
        }
    }
}
