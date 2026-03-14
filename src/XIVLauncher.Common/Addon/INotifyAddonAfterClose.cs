namespace XIVLauncher.Common.Addon;

internal interface INotifyAddonAfterClose : IAddon
{
    void GameClosed();
}
