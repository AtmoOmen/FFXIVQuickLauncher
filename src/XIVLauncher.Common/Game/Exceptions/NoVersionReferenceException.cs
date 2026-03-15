using System;

namespace XIVLauncher.Common.Game.Exceptions;

public class NoVersionReferenceException
(
    Repository repo,
    string     version
) : Exception($"未找到 {repo}({version}) 对应的版本信息文件");
