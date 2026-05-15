namespace XIVLauncher.Login;

public interface ILoginSessionRefreshSink
{
    void Bind(LoginSessionRefreshContext context);
}
