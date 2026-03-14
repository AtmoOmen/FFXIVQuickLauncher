#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XIVLauncher.Common.Game;

namespace XIVLauncher.Common.Patching.SdoFileDownload;

public interface ISdoFileDownloadInstaller : IDisposable, IInstaller
{
    Task ConstructFromRemoteIntegrity(IntegrityCheck.IntegrityCheckResult remoteIntegrity, TimeSpan progressReportInterval = default);

    Task VerifyFiles(string gameRootPath, bool refine = false, int concurrentCount = 8, CancellationToken cancellationToken = default);

    Task QueueInstall(int targetIndex, string filePath, CancellationToken cancellationToken = default);

    Task Install(int concurrentCount = 8, CancellationToken cancellationToken = default);

    Task<List<string>> GetBrokenFiles(CancellationToken cancellationToken = default);

    event SdoFileDownloadInstaller.OnInstallProgressDelegate? OnInstallProgress;
    event SdoFileDownloadInstaller.OnVerifyProgressDelegate?  OnVerifyProgress;
}
