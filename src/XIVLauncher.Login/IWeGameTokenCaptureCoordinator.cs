namespace XIVLauncher.Login;

public interface IWeGameTokenCaptureCoordinator
{
    Task<WeGameCaptureResult?> CaptureAsync
    (
        ILoginWorkflowInteraction interaction,
        CancellationTokenSource   loginCancellationTokenSource
    );
}
