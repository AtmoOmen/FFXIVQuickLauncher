using System.IO;
using XIVLauncher.Common.Unix.Compatibility.GameFixes.Implementations;

namespace XIVLauncher.Common.Unix.Compatibility.GameFixes;

public class GameFixApply
{
    private readonly GameFix[] fixes;

    public GameFixApply(DirectoryInfo gameDirectory, DirectoryInfo configDirectory, DirectoryInfo winePrefixDirectory, DirectoryInfo tempDirectory)
    {
        fixes = new GameFix[]
        {
            new CutsceneMovieOpeningFix(gameDirectory, configDirectory, winePrefixDirectory, tempDirectory)
        };
    }

    public void Run()
    {
        foreach (var fix in fixes)
        {
            UpdateProgress?.Invoke(fix.LoadingTitle, false, 0f);

            fix.UpdateProgress += UpdateProgress;
            fix.Apply();
            fix.UpdateProgress -= UpdateProgress;
        }
    }

    public delegate void UpdateProgressDelegate(string loadingText, bool hasProgress, float progress);

    public event UpdateProgressDelegate UpdateProgress;
}
