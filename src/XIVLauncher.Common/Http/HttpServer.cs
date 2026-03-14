using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace XIVLauncher.Common.Http;

// This is a very dumb HTTP server that just accepts GETs and fires events with the requested URL
internal class HttpServer
{
    public           EventHandler<HttpServerGetEvent> GetReceived;
    private readonly TcpListener                      listener;

    private readonly byte[] httpResponse;

    private bool _isRunning;

    public HttpServer(int port, string version)
    {
        listener = new TcpListener(IPAddress.Any, port);

        httpResponse = Encoding.Default.GetBytes
        (
            "HTTP/1.0 200 OK\n" + "Content-Type: application/json; charset=UTF-8\n" + "\n{\"app\":\"XIVLauncher\", \"version\":\"" + version + "\"}"
        );
    }

    public void Start()
    {
        try
        {
            listener.Start();
            _isRunning = true;

            while (_isRunning)
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(200);
                    continue;
                }

                var client = listener.AcceptTcpClient();

                while (client.Connected)
                {
                    var networkStream = client.GetStream();

                    var message = new byte[1024];
                    networkStream.Read(message, 0, message.Length);

                    var messageString = Encoding.Default.GetString(message);
                    Debug.WriteLine(Encoding.Default.GetString(message));

                    networkStream.Write(httpResponse, 0, httpResponse.Length);

                    networkStream.Close(3);

                    GetReceived?.Invoke
                    (
                        this,
                        new HttpServerGetEvent
                        {
                            Path = Regex.Match(messageString, "GET (?<url>.+) HTTP").Groups["url"].Value
                        }
                    );
                }

                client.Close();
            }
        }
        catch
        {
            // ignored
        }
    }

    public void Stop()
    {
        _isRunning = false;
        listener.Stop();
    }

    public class HttpServerGetEvent
    {
        public string Path { get; set; }
    }
}
