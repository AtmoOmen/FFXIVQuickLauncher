using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Http;

public class HappyEyeballsCallback
{
    private const int ConnectionBackoff = 100;

    public static async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var sortedRecords = await DnsResolver.GetSortedAddressesAsync(context.DnsEndPoint.Host, token);

        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task<NetworkStream>>();

        var delayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken.Token);

        for (var i = 0; i < sortedRecords.Count; i++)
        {
            var record = sortedRecords[i];

            delayCts.CancelAfter(ConnectionBackoff * i);

            var task = AttemptConnection(record, context.DnsEndPoint.Port, linkedToken.Token, delayCts.Token);
            tasks.Add(task);

            var nextDelayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken.Token);
            _ = task.ContinueWith(_ => { nextDelayCts.Cancel(); }, TaskContinuationOptions.OnlyOnFaulted);
            delayCts = nextDelayCts;
        }

        var stream = await AsyncUtils.FirstSuccessfulTask(tasks).ConfigureAwait(false);

        // If we're here, it means we have a successful connection. A failure to connect would have caused the above
        // line to explode, so we're safe to clean everything up.
        linkedToken.Cancel();
        tasks.ForEach(task => { task.ContinueWith(CleanupConnectionTask); });
        return stream;
    }

    private static async Task<NetworkStream> AttemptConnection(
        IPAddress address, int port, CancellationToken token,
        CancellationToken delayToken)
    {
        await AsyncUtils.CancellableDelay(-1, delayToken).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(address, port, token).ConfigureAwait(false);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void CleanupConnectionTask(Task task)
    {
        // marks the exception as handled as well, nifty!
        // will also handle canceled cases, which aren't explicitly faulted.
        var exception = task.Exception;

        if (task.IsFaulted)
        {
            Log.Verbose(exception!, "A HappyEyeballs connection task failed. Are there network issues?");
        }
    }
}
