namespace XIVLauncher.Dalamud;

public interface IDalamudService
{
    DalamudUpdater Updater { get; }

    event Action<DalamudStatusSnapshot>? StatusChanged;

    void RunUpdater(bool refreshVersionInfo = false);

    void EnsureCompatibility();

    DalamudSession CreateLauncher(DirectoryInfo gamePath, DalamudLaunchOptions options);

    DalamudStatusSnapshot GetStatusSnapshot();
}
