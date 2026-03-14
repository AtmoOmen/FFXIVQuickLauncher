namespace XIVLauncher.Common.Addon;

internal interface IPersistentAddon : IAddon
{
    void DoWork(object state);
}
