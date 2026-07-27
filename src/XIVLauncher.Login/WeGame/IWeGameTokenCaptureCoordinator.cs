using XIVLauncher.Login.Workflow;

namespace XIVLauncher.Login.WeGame;

public interface IWeGameTokenCaptureCoordinator
{
    Task<WeGameCaptureResult?> CaptureAsync
    (
        ILoginWorkflowInteraction interaction,
        CancellationTokenSource   loginCancellationTokenSource
    );
}
