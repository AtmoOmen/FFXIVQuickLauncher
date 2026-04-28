using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game.Integrity;

namespace XIVLauncher.Common.Patching.SdoFileDownload;

public interface ISdoFileDownloadInstaller : IDisposable, IInstaller
{
    Task ConstructFromRemoteIntegrity(IntegrityCheckResult remoteIntegrity, TimeSpan progressReportInterval = default);

    Task VerifyFiles(string gameRootPath, bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default);

    Task QueueInstall(int targetIndex, string filePath, CancellationToken cancellationToken = default);

    Task Install(int concurrentCount = 8, CancellationToken cancellationToken = default);

    Task ApplyVcdiff(string sourceFile, string deltaFile, string targetFile, string expectedMd5, long expectedSize, CancellationToken cancellationToken = default);

    Task<List<string>> GetBrokenFiles(CancellationToken cancellationToken = default);

    Task WriteAllText(string filePath, string content, CancellationToken cancellationToken = default);

    event SdoFileDownloadInstaller.OnInstallProgressDelegate? OnInstallProgress;

    event SdoFileDownloadInstaller.OnVerifyProgressDelegate? OnVerifyProgress;
}
