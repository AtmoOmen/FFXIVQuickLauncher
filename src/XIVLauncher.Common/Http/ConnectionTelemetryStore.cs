using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace XIVLauncher.Common.Http;

internal static class ConnectionTelemetryStore
{
    private static readonly ConcurrentDictionary<ConnectionTargetKey, EndpointTelemetry> EndpointTelemetryByTarget = [];

    private static readonly ConcurrentDictionary<ConnectionFamilyKey, EndpointTelemetry> FamilyTelemetryByTarget = [];

    public static IReadOnlyList<ConnectionCandidate> SortCandidates(string host, IReadOnlyList<ConnectionCandidate> candidates)
    {
        if (candidates.Count <= 1)
            return candidates;

        var ordered = new List<ConnectionCandidate>(candidates);
        var now     = Environment.TickCount64;

        ordered.Sort((left, right) => CompareCandidates(host, left, right, now));
        return ordered;
    }

    public static void ReportFailure(string host, ConnectionCandidate candidate)
    {
        var key       = new ConnectionTargetKey(host, candidate.Address);
        var telemetry = EndpointTelemetryByTarget.GetOrAdd(key, static _ => new EndpointTelemetry());
        telemetry.RecordFailure();

        var familyKey       = new ConnectionFamilyKey(host, candidate.Address.AddressFamily);
        var familyTelemetry = FamilyTelemetryByTarget.GetOrAdd(familyKey, static _ => new EndpointTelemetry());
        familyTelemetry.RecordFailure();

    }

    public static void ReportSuccess(string host, ConnectionCandidate candidate, TimeSpan latency)
    {
        var key       = new ConnectionTargetKey(host, candidate.Address);
        var telemetry = EndpointTelemetryByTarget.GetOrAdd(key, static _ => new EndpointTelemetry());

        telemetry.RecordSuccess(latency);

        var familyKey       = new ConnectionFamilyKey(host, candidate.Address.AddressFamily);
        var familyTelemetry = FamilyTelemetryByTarget.GetOrAdd(familyKey, static _ => new EndpointTelemetry());
        familyTelemetry.RecordSuccess(latency);

    }

    private static int CompareCandidates
    (
        string              host,
        ConnectionCandidate left,
        ConnectionCandidate right,
        long                now
    )
    {
        var leftScore  = GetScore(host, left,  now);
        var rightScore = GetScore(host, right, now);

        return leftScore != rightScore ? leftScore.CompareTo(rightScore) : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static long GetScore
    (
        string              host,
        ConnectionCandidate candidate,
        long                now
    )
    {
        var score = BASELINE_SCORE;

        var key = new ConnectionTargetKey(host, candidate.Address);

        var familyKey = new ConnectionFamilyKey(host, candidate.Address.AddressFamily);

        if (FamilyTelemetryByTarget.TryGetValue(familyKey, out var familyTelemetry))
            score = familyTelemetry.ApplyTo(score, now, FAMILY_LATENCY_PERCENT, FAMILY_TELEMETRY_PERCENT);

        if (EndpointTelemetryByTarget.TryGetValue(key, out var telemetry))
            score = telemetry.ApplyTo(score, now, ENDPOINT_LATENCY_PERCENT, ENDPOINT_TELEMETRY_PERCENT);

        return score;
    }

    private sealed class EndpointTelemetry
    {
        private long consecutiveFailures;
        private long lastFailureTick;
        private long lastSuccessTick;
        private long smoothedLatencyMs;
        private long successCount;

        public long ApplyTo(long baselineScore, long now, long latencyPercent, long telemetryPercent)
        {
            var score = baselineScore;

            var latencyMs = Interlocked.Read(ref smoothedLatencyMs);
            if (latencyMs > 0)
                score = latencyMs * latencyPercent / PERCENT_SCALE;

            var successTicks = Interlocked.Read(ref lastSuccessTick);
            if (successTicks > 0 && now - successTicks <= RECENT_SUCCESS_WINDOW_MS)
                score -= RECENT_SUCCESS_BONUS * telemetryPercent / PERCENT_SCALE;

            var successes = Interlocked.Read(ref successCount);
            if (successes > 0)
                score -= Math.Min(successes, MAX_SUCCESS_BONUS_STEPS) * SUCCESS_BONUS_STEP * telemetryPercent / PERCENT_SCALE;

            var failureTicks = Interlocked.Read(ref lastFailureTick);
            var failures     = Interlocked.Read(ref consecutiveFailures);

            if (failureTicks <= 0 || failures <= 0)
                return score;

            var failureAge = now - failureTicks;

            if (failureAge <= HARD_FAILURE_WINDOW_MS)
                return score + failures * HARD_FAILURE_PENALTY * telemetryPercent / PERCENT_SCALE;

            if (failureAge <= SOFT_FAILURE_WINDOW_MS)
                score += failures * SOFT_FAILURE_PENALTY * telemetryPercent / PERCENT_SCALE;

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

    private readonly record struct ConnectionFamilyKey
    (
        string        Host,
        AddressFamily AddressFamily
    );

    #region Constants

    private const long BASELINE_SCORE = 700;

    private const long ENDPOINT_LATENCY_PERCENT = 100;

    private const long ENDPOINT_TELEMETRY_PERCENT = 100;

    private const long FAMILY_LATENCY_PERCENT = 145;

    private const long FAMILY_TELEMETRY_PERCENT = 50;

    private const long HARD_FAILURE_PENALTY = 1_600;

    private const long HARD_FAILURE_WINDOW_MS = 60_000;

    private const long LATENCY_SMOOTHING_DIVISOR = 10;

    private const long LATENCY_SMOOTHING_WEIGHT = 7;

    private const long MAX_RECORDED_LATENCY_MS = 10_000;

    private const long MAX_SUCCESS_BONUS_STEPS = 8;

    private const long MIN_RECORDED_LATENCY_MS = 1;

    private const long PERCENT_SCALE = 100;

    private const long RECENT_SUCCESS_BONUS = 220;

    private const long RECENT_SUCCESS_WINDOW_MS = 3 * 60 * 1000;

    private const long SOFT_FAILURE_PENALTY = 600;

    private const long SOFT_FAILURE_WINDOW_MS = 5 * 60 * 1000;

    private const long SUCCESS_BONUS_STEP = 20;

    #endregion
}
