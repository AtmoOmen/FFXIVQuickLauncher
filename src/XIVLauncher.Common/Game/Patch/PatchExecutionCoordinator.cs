using XIVLauncher.Common.Game.Login;
using XIVLauncher.Common.Game.Patch.PatchList;

namespace XIVLauncher.Common.Game.Patch;

public static class PatchExecutionCoordinator
{
    public static PatchListEntry[] GetPendingPatchesForInstall(LoginResult loginResult)
    {
        if (loginResult.State != LoginState.NeedsPatchGame)
            throw new ArgumentException(@"loginResult.State != Launcher.LoginState.NeedsPatchGame", nameof(loginResult));

        if (loginResult.PendingPatches == null)
            throw new ArgumentException(@"loginResult.PendingPatches == null", nameof(loginResult));

        if (loginResult.PendingPatches.Length == 0)
            throw new ArgumentException(@"loginResult.PendingPatches.Length == 0", nameof(loginResult));

        return loginResult.PendingPatches;
    }

    public static async Task<PatchExecutionResult> ExecuteAsync(PatchExecutionRequest request)
    {
        using var mutex = new Mutex(false, request.MutexName);

        if (!mutex.WaitOne(0, false))
            return new PatchExecutionResult { Status = PatchExecutionStatus.AlreadyRunning };

        if (request.IsGameOpen())
        {
            while (request.IsGameOpen())
            {
                if (!request.ContinueWhenGameOpen())
                    return new PatchExecutionResult { Status = PatchExecutionStatus.CancelledByUser };
            }
        }

        if (!request.EnsureGameFilesClosed())
            return new PatchExecutionResult { Status = PatchExecutionStatus.CancelledByUser };

        try
        {
            await request.Patcher.PatchAsync(request.AriaLogFile).ConfigureAwait(false);
            return new PatchExecutionResult { Status = PatchExecutionStatus.Success };
        }
        catch (OperationCanceledException)
        {
            return new PatchExecutionResult { Status = PatchExecutionStatus.CancelledByUser };
        }
        catch (PatchInstallerException ex)
        {
            return new PatchExecutionResult { Status = PatchExecutionStatus.PatchInstallerError, Exception = ex };
        }
        catch (NotEnoughSpaceException ex)
        {
            return new PatchExecutionResult { Status = PatchExecutionStatus.NotEnoughSpace, Exception = ex };
        }
        catch (Exception ex)
        {
            return new PatchExecutionResult { Status = PatchExecutionStatus.Failed, Exception = ex };
        }
    }
}
