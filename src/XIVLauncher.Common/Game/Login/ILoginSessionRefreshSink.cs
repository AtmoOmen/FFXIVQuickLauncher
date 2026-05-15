namespace XIVLauncher.Common.Game.Login;

public interface ILoginSessionRefreshSink
{
    void Bind(LoginSessionRefreshContext context);
}
