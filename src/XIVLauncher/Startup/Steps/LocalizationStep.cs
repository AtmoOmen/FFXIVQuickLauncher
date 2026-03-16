using System;
using System.Threading;
using System.Threading.Tasks;
using CheapLoc;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Settings;

namespace XIVLauncher.Startup.Steps;

public class LocalizationStep : IStartupStep
{
    public string Name => "本地化初始化";
    public int Order => 50;

    public Task ExecuteAsync(StartupContext context, CancellationToken cancellationToken = default)
    {
#if !XL_LOC_FORCEFALLBACKS
        try
        {
            context.Settings.LauncherLanguage ??= LauncherLanguage.SimplifiedChinese;

            Log.Information("正在为语言代码 {0} 设置本地化", context.Settings.LauncherLanguage.GetLocalizationCode());

            if (!context.Settings.LauncherLanguage.IsDefault())
                Loc.Setup(AppUtil.GetFromResources($"XIVLauncher.Resources.Loc.xl.xl_{context.Settings.LauncherLanguage.GetLocalizationCode()}.json"));
            else
                Loc.SetupWithFallbacks();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "无法获取语言信息, 使用回退设置");
            Loc.Setup("{}");
        }
#else
        Loc.Setup("{}");
#endif

        return Task.CompletedTask;
    }
}
