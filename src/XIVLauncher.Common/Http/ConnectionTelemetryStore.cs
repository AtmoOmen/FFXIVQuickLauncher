using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace XIVLauncher.Common.Http;

internal static class ConnectionTelemetryStore
{
    private static readonly ConcurrentDictionary<ConnectionTargetKey, EndpointTelemetry> EndpointTelemetryByTarget = [];

    private static readonly ConcurrentDictionary<string, PreferredTarget> PreferredTargetByHost =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ConnectionCandidate> SortCandidates(string host, IReadOnlyList<ConnectionCandidate> candidates)
    {
        if (candidates.Count <= 1)
            return candidates;

        var ordered = new List<ConnectionCandidate>(candidates);
        var now     = Environment.TickCount64;

        PreferredTargetByHost.TryGetValue(host, out var preferredTarget);

        ordered.Sort((left, right) => CompareCandidates(host, left, right, preferredTarget, now));
        return ordered;
    }

    public static void ReportFailure(string host, ConnectionCandidate candidate)
    {
        var key       = new ConnectionTargetKey(host, candidate.Address);
        var telemetry = EndpointTelemetryByTarget.GetOrAdd(key, static _ => new EndpointTelemetry());
        telemetry.RecordFailure();
    }

    public static void ReportSuccess(string host, ConnectionCandidate candidate, TimeSpan latency)
    {
        var key       = new ConnectionTargetKey(host, candidate.Address);
        var telemetry = EndpointTelemetryByTarget.GetOrAdd(key, static _ => new EndpointTelemetry());

        telemetry.RecordSuccess(latency);
        PreferredTargetByHost[host] = new PreferredTarget(candidate.Address, Environment.TickCount64 + HOST_PREFERRED_TTL_MS);
    }

    private static int CompareCandidates
    (
        string              host,
        ConnectionCandidate left,
        ConnectionCandidate right,
        PreferredTarget     preferredTarget,
        long                now
    )
    {
        var leftScore  = GetScore(host, left,  preferredTarget, now);
        var rightScore = GetScore(host, right, preferredTarget, now);

        return leftScore != rightScore ? leftScore.CompareTo(rightScore) : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static long GetScore
    (
        string              host,
        ConnectionCandidate candidate,
        PreferredTarget     preferredTarget,
        long                now
    )
    {
        var score = candidate.Source == ConnectionCandidateSource.HijackDns ? HIJACK_BASELINE_SCORE : DIRECT_BASELINE_SCORE;

        if (preferredTarget.IsActive(now) && preferredTarget.Address?.Equals(candidate.Address) == true)
            score -= HOST_PREFERRED_BONUS;

        var key = new ConnectionTargetKey(host, candidate.Address);

        if (EndpointTelemetryByTarget.TryGetValue(key, out var telemetry))
            score = telemetry.ApplyTo(score, now);

        return score;
    }

    private sealed class EndpointTelemetry
    {
        private long consecutiveFailures;
        private long lastFailureTick;
        private long lastSuccessTick;
        private long smoothedLatencyMs;
        private long successCount;

        public long ApplyTo(long baselineScore, long now)
        {
            var score = baselineScore;

            var latencyMs = Interlocked.Read(ref smoothedLatencyMs);
            if (latencyMs > 0)
                score = latencyMs;

            var successTicks = Interlocked.Read(ref lastSuccessTick);
            if (successTicks > 0 && now - successTicks <= RECENT_SUCCESS_WINDOW_MS)
                score -= RECENT_SUCCESS_BONUS;

            var successes = Interlocked.Read(ref successCount);
            if (successes > 0)
                score -= Math.Min(successes, MAX_SUCCESS_BONUS_STEPS) * SUCCESS_BONUS_STEP;

            var failureTicks = Interlocked.Read(ref lastFailureTick);
            var failures     = Interlocked.Read(ref consecutiveFailures);

            if (failureTicks <= 0 || failures <= 0)
                return score;

            var failureAge = now - failureTicks;

            if (failureAge <= HARD_FAILURE_WINDOW_MS)
                return score + failures * HARD_FAILURE_PENALTY;

            if (failureAge <= SOFT_FAILURE_WINDOW_MS)
                score += failures * SOFT_FAILURE_PENALTY;

            return score;
        }

        public void RecordFailure()
        {
            Interlocked.Increment(ref consecutiveFailures);
            Interlocked.Exchange(ref lastFailureTick, Environment.TickCount64);
        }

        public void RecordSuccess(TimeSpan latency)
        {
            var latencyMs = Math.Clamp((long)Math.Round(latency.TotalMilliseconds), MIN_RECORDED_LATENCY_MS, MAX_RECORDED_LATENCY_MS);

            Interlocked.Exchange(ref consecutiveFailures, 0);
            Interlocked.Exchange(ref lastSuccessTick, Environment.TickCount64);
            Interlocked.Increment(ref successCount);

            while (true)
            {
                var snapshot = Interlocked.Read(ref smoothedLatencyMs);
                var updated  = snapshot == 0 ? latencyMs : (snapshot * LATENCY_SMOOTHING_WEIGHT + latencyMs) / LATENCY_SMOOTHING_DIVISOR;

                if (Interlocked.CompareExchange(ref smoothedLatencyMs, updated, snapshot) == snapshot)
                    return;
            }
        }
    }

    private readonly record struct PreferredTarget
    (
        IPAddress? Address,
        long       ExpiresAtTick
    )
    {
        public bool IsActive(long now) =>
            Address != null && ExpiresAtTick >= now;
    }

    #region Constants

    private const long DIRECT_BASELINE_SCORE = 700;

    private const long HIJACK_BASELINE_SCORE = 480;

    private const long HARD_FAILURE_PENALTY = 1_600;

    private const long HARD_FAILURE_WINDOW_MS = 60_000;

    private const long HOST_PREFERRED_BONUS = 900;

    private const long HOST_PREFERRED_TTL_MS = 10 * 60 * 1000;

    private const long LATENCY_SMOOTHING_DIVISOR = 10;

    private const long LATENCY_SMOOTHING_WEIGHT = 7;

    private const long MAX_RECORDED_LATENCY_MS = 10_000;

    private const long MAX_SUCCESS_BONUS_STEPS = 8;

    private const long MIN_RECORDED_LATENCY_MS = 1;

    private const long RECENT_SUCCESS_BONUS = 220;

    private const long RECENT_SUCCESS_WINDOW_MS = 3 * 60 * 1000;

    private const long SOFT_FAILURE_PENALTY = 600;

    private const long SOFT_FAILURE_WINDOW_MS = 5 * 60 * 1000;

    private const long SUCCESS_BONUS_STEP = 20;

    #endregion
}
