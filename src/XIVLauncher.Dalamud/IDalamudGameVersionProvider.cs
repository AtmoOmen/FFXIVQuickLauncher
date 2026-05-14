namespace XIVLauncher.Dalamud;

public interface IDalamudGameVersionProvider
{
    string GetVersion(DirectoryInfo gamePath, bool isBck = false);
}
