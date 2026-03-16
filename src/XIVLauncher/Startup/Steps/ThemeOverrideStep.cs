using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using XIVLauncher.Common;
using XIVLauncher.Settings;

namespace XIVLauncher.Startup.Steps;

public class ThemeOverrideStep : IStartupStep
{
    private static readonly Brush UABrush = new LinearGradientBrush
    (
        [
            new(Color.FromArgb(0xFF, 0xFF, 0x4D, 0x00), 0.0f),
            new(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00), 1.0f)
        ],
        0.7f
    );

    private readonly Application application;

    public ThemeOverrideStep(Application application)
    {
        this.application = application;
    }

    public string Name => "主题覆盖";
    public int Order => 80;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (context.Settings.LauncherLanguage == LauncherLanguage.Russian)
            {
                var dict = new ResourceDictionary
                {
                    { "PrimaryHueLightBrush", UABrush },
                    { "PrimaryHueMidBrush", UABrush },
                    { "PrimaryHueDarkBrush", UABrush }
                };
                application.Resources.MergedDictionaries.Add(dict);
            }
        }
        catch
        {
            // ignored
        }

        return Task.CompletedTask;
    }
}
