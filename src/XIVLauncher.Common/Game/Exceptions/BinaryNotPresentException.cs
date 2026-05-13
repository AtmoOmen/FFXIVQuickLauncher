namespace XIVLauncher.Common.Game.Exceptions;

public class BinaryNotPresentException
(
    string path
) : Exception("未找到游戏二进制文件")
{
    public string Path { get; private set; } = path;
}
