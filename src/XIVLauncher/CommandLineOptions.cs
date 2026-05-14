using CommandLine;

namespace XIVLauncher;

public class CommandLineOptions
{
    [Option("account", Required = false, HelpText = "要使用的账号名称")]
    public string AccountName { get; set; } = null!;
}
