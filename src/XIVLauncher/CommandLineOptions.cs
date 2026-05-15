using CommandLine;

namespace XIVLauncher;

public class CommandLineOptions
{
    [Option("roamingPath", Required = false, HelpText = "指定软件工作路径")]
    public string RoamingPath { get; set; } = null!;
    
    [Option("account", Required = false, HelpText = "指定使用的账号名称")]
    public string AccountName { get; set; } = null!;
}
