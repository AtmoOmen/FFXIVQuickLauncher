namespace XIVLauncher.Common.Game.DCTravel;

public class DCTravelOrderInfo
{
    /// <summary>
    ///     订单状态
    /// </summary>
    public DCTravelStatusType Status { get; set; }

    /// <summary>
    ///     检查阶段的详细信息
    /// </summary>
    public string CheckMessage { get; set; } = null!;

    /// <summary>
    ///     迁移/处理阶段的详细信息
    /// </summary>
    public string MigrationMessage { get; set; } = null!;
}
