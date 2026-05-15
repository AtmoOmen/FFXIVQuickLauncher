namespace XIVLauncher.DCTravel;

public enum DCTravelStatusType
{
    TravelFailed     = -5,
    PreCheckFailed   = -1,
    Checking         = 0,
    CheckingAlt      = 1,
    NeedConfirmation = 2,
    Processing       = 3,
    ProcessingAlt    = 4,
    Success          = 5
}
