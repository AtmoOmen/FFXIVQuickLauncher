using System;
using System.Threading;

namespace XIVLauncher.Common.Http;

public class OtpListener
{
    private const int HTTP_PORT = 4646;

    private readonly Thread     serverThread;
    private volatile HttpServer server;

    public OtpListener(string version)
    {
        server             =  new HttpServer(HTTP_PORT, version);
        server.GetReceived += GetReceived;

        serverThread = new Thread(server.Start) { Name = "OtpListenerServerThread", IsBackground = true };
    }

    public void Start() =>
        serverThread.Start();

    public void Stop() =>
        server?.Stop();

    private void GetReceived(object sender, HttpServer.HttpServerGetEvent e)
    {
        if (e.Path.StartsWith("/ffxivlauncher/", StringComparison.Ordinal))
        {
            var otp = e.Path.Substring(15);

            OnOtpReceived?.Invoke(otp);
        }
    }

    public event LoginEvent OnOtpReceived;

    public delegate void LoginEvent(string onetimePassword);
}
