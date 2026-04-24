using CommandLine;
using XIVLauncher.Common;

namespace XIVLauncher;

public class CommandLineOptions
{
    [Option("dalamud-runner-override", Required = false, HelpText = "用于覆盖 Dalamud 运行器的文件夹路径")]
    public string RunnerOverride { get; set; } = null!;

    [Option("roamingPath", Required = false, HelpText = "用于覆盖 XIVLauncher 漫游路径的文件夹路径")]
    public string RoamingPath { get; set; } = null!;

    [Option("gen-localizable", Required = false, HelpText = "生成本地化文件")]
    public bool DoGenerateLocalizables { get; set; }

    [Option("account", Required = false, HelpText = "要使用的账号名称")]
    public string AccountName { get; set; } = null!;

    [Option("clientlang", Required = false, HelpText = "要使用的客户端语言")]
    public ClientLanguage? ClientLanguage { get; set; }

    [Option("squirrel-updated", Hidden = true)]
    public string SquirrelUpdated { get; set; } = null!;

    [Option("squirrel-install", Hidden = true)]
    public string SquirrelInstall { get; set; } = null!;

    [Option("squirrel-obsolete", Hidden = true)]
    public string SquirrelObsolete { get; set; } = null!;

    [Option("squirrel-uninstall", Hidden = true)]
    public string SquirrelUninstall { get; set; } = null!;

    [Option("squirrel-firstrun", Hidden = true)]
    public bool SquirrelFirstRun { get; set; }
}
