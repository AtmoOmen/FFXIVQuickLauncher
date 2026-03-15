namespace XIVLauncher.Common.Game.DCTravel;

public class DCTravelMigrationOrders
{
    public int                      TotalCount   { get; set; }
    public int                      TotalPageNum { get; set; }
    public DCTravelMigrationOrder[] Orders       { get; set; } = [];
}
