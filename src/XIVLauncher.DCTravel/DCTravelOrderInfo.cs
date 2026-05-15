namespace XIVLauncher.DCTravel;

public class DCTravelOrderInfo
{
    public DCTravelStatusType Status           { get; set; }
    public string             CheckMessage     { get; set; } = null!;
    public string             MigrationMessage { get; set; } = null!;
}
