using System;
using System.Windows.Input;

namespace XIVLauncher.Windows.ViewModel;

internal class GameRepairProgressWindowViewModel
{
    public ICommand CancelCommand { get; set; }

    public string FormatEstimatedTime(long remaining, long speed)
    {
        if (speed == 0)
            return $"还有 {99:00}:{59:00}:{59:00}";

        var remainingSecs = (int)Math.Ceiling(1.0 * remaining / speed);
        remainingSecs = Math.Min(remainingSecs, 60 * 60 * 100 - 1);
        if (remainingSecs < 60 * 60)
            return $"还有 {remainingSecs / 60:00}:{remainingSecs % 60:00}";

        return $"还有 {remainingSecs / 60 / 60:00}:{remainingSecs / 60 % 60:00}:{remainingSecs % 60:00}";
    }
}
