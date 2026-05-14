using Serilog;
using XIVLauncher.VcdiffShim;

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: VcdiffShim.exe <parentProcessId> <channelName>");
    return 1;
}

if (!int.TryParse(args[0], out var parentProcessId))
{
    Console.Error.WriteLine("无效的父进程 ID");
    return 1;
}

var channelName = args[1];

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "vcdiff-shim.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    Log.Information("[VcdiffShim] 启动, 父进程 {ParentProcessId}, 通道 {ChannelName}", parentProcessId, channelName);
    using var worker = new VcdiffWorker(parentProcessId, channelName);
    worker.Run();
    Log.Information("[VcdiffShim] 退出");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "[VcdiffShim] 致命错误");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
