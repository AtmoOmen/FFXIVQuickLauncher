namespace XIVLauncher.Login.Workflow;

public interface ILoginSessionRefreshSink
{
    void Bind(LoginSessionRefreshContext context);
}
