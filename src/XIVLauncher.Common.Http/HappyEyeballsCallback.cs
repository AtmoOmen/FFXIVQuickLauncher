using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Serilog;

namespace XIVLauncher.Common.Http;

public static class HappyEyeballsCallback
{
    public static async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var candidates = await DNSResolver.GetSortedAddressesAsync(context.DnsEndPoint.Host, context.DnsEndPoint.AddressFamily, token).ConfigureAwait(false);

        if (candidates.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        using var winnerCts  = new CancellationTokenSource();
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token, winnerCts.Token);

        var attempts = new List<Task<NetworkStream>>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var delay     = GetAttemptDelay(candidates, i);
            var attempt   = AttemptConnectionAsync(context.DnsEndPoint.Host, candidate, context.DnsEndPoint.Port, delay, token, winnerCts.Token, attemptCts.Token);

            ObserveAttemptFailure(attempt, context.DnsEndPoint.Host, candidate);
            attempts.Add(attempt);
        }

        try
        {
            var stream = await WaitForFirstSuccessfulAttemptAsync(attempts).ConfigureAwait(false);
            winnerCts.Cancel();
            return stream;
        }
        catch
        {
            winnerCts.Cancel();
            throw;
        }
    }

    private static async Task<NetworkStream> AttemptConnectionAsync
    (
        string              host,
        ConnectionCandidate candidate,
        int                 port,
        TimeSpan            delay,
        CancellationToken   callerToken,
        CancellationToken   winnerToken,
        CancellationToken   attemptToken
    )
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, attemptToken).ConfigureAwait(false);

        attemptToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var socket    = new Socket(candidate.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(candidate.Address, port, attemptToken).ConfigureAwait(false);
            stopwatch.Stop();

            ConnectionTelemetryStore.ReportSuccess(host, candidate, stopwatch.Elapsed);

            Log.Verbose
            (
                "HTTP 连接命中 {Host} -> {Address} [{Source}], 建连耗时 {ElapsedMs:0} ms",
                host,
                candidate.Address,
                candidate.Source,
                stopwatch.Elapsed.TotalMilliseconds
            );

            return new NetworkStream(socket, true);
        }
        catch (OperationCanceledException) when (winnerToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch
        {
            ConnectionTelemetryStore.ReportFailure(host, candidate);
            socket.Dispose();
            throw;
        }
    }

    private static TimeSpan GetAttemptDelay(IReadOnlyList<ConnectionCandidate> candidates, int index)
    {
        if (index <= 0)
            return TimeSpan.Zero;

        var candidate = candidates[index];
        var previous  = candidates[index - 1];

        if (IPAddress.IsLoopback(candidate.Address) || IPAddress.IsLoopback(previous.Address))
            return TimeSpan.FromMilliseconds(LOCAL_FALLBACK_DELAY_MS + (index - 1L) * LOCAL_ADDITIONAL_FALLBACK_DELAY_MS);

        var delayMs = FIRST_FALLBACK_DELAY_MS + (index - 1L) * ADDITIONAL_FALLBACK_DELAY_MS;

        if (previous.Source == ConnectionCandidateSource.DirectDns && candidate.Source == ConnectionCandidateSource.CloudflareRange)
            delayMs -= CROSS_SOURCE_ACCELERATION_MS;
        else if (previous.Source == ConnectionCandidateSource.CloudflareRange && candidate.Source == ConnectionCandidateSource.DirectDns)
            delayMs += CROSS_SOURCE_FALLBACK_DELAY_MS;

        if (candidate.Address.AddressFamily != previous.Address.AddressFamily)
            delayMs -= CROSS_FAMILY_ACCELERATION_MS;

        return TimeSpan.FromMilliseconds(Math.Max(MIN_FALLBACK_DELAY_MS, delayMs));
    }

    private static void ObserveAttemptFailure(Task<NetworkStream> attempt, string host, ConnectionCandidate candidate)
    {
        _ = attempt.ContinueWith
        (
            static (completedTask, state) =>
            {
                var (observedHost, observedCandidate) = ((string Host, ConnectionCandidate Candidate))state!;

                if (completedTask.Exception is { } exception)
                {
                    Log.Verbose
                    (
                        exception.Flatten(),
                        "HappyEyeballs 连接失败: {Host} -> {Address} [{Source}]",
                        observedHost,
                        observedCandidate.Address,
                        observedCandidate.Source
                    );
                }
            },
            (host, candidate),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static async Task<NetworkStream> WaitForFirstSuccessfulAttemptAsync(IReadOnlyList<Task<NetworkStream>> attempts)
    {
        var pendingAttempts   = new List<Task<NetworkStream>>(attempts);
        var canceledException = default(OperationCanceledException);
        var failedExceptions  = default(List<Exception>);

        while (pendingAttempts.Count > 0)
        {
            var completedAttempt = await Task.WhenAny(pendingAttempts).ConfigureAwait(false);
            pendingAttempts.Remove(completedAttempt);

            try
            {
                return await completedAttempt.ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                canceledException ??= ex;
            }
            catch (Exception ex)
            {
                failedExceptions ??= [];
                failedExceptions.Add(ex);
            }
        }

        if (failedExceptions is { Count: > 0 })
            throw new AggregateException(failedExceptions);

        if (canceledException != null)
            throw canceledException;

        throw new SocketException((int)SocketError.NotConnected);
    }

    #region Constants

    private const int ADDITIONAL_FALLBACK_DELAY_MS = 55;

    private const int CROSS_FAMILY_ACCELERATION_MS = 12;

    private const int CROSS_SOURCE_ACCELERATION_MS = 18;

    private const int CROSS_SOURCE_FALLBACK_DELAY_MS = 140;

    private const int FIRST_FALLBACK_DELAY_MS = 35;

    private const int LOCAL_ADDITIONAL_FALLBACK_DELAY_MS = 8;

    private const int LOCAL_FALLBACK_DELAY_MS = 8;

    private const int MIN_FALLBACK_DELAY_MS = 5;

    #endregion
}
