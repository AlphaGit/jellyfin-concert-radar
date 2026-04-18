using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.RateLimiting;

/// <summary>
/// Singleton rate limiter keyed by source id.  Each source gets its own
/// <see cref="TokenBucketRateLimiter"/> built from <see cref="PluginConfiguration.RateLimits"/>
/// with hard-coded spec defaults as a fallback.  Limiters are constructed lazily on first use.
/// </summary>
public sealed class HostRateLimiter : IAsyncDisposable
{
    // Spec §5 default rate limits.
    private static readonly IReadOnlyDictionary<string, RateLimitConfig> DefaultLimits =
        new Dictionary<string, RateLimitConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["ticketmaster"] = new() { RequestsPerSecond = 5,   RequestsPerDay = 5000 },
            ["bandsintown"]  = new() { RequestsPerSecond = 1,   RequestsPerDay = null },
            ["edmtrain"]     = new() { RequestsPerSecond = 1,   RequestsPerDay = null },
            ["songkick"]     = new() { RequestsPerSecond = 1,   RequestsPerDay = null },
            ["dice"]         = new() { RequestsPerSecond = 0.5, RequestsPerDay = null },
            ["ra"]           = new() { RequestsPerSecond = 0.5, RequestsPerDay = null },
            // MusicBrainz ToS requires ≤1 req/s.
            ["musicbrainz"]  = new() { RequestsPerSecond = 1,   RequestsPerDay = null },
        };

    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly SourceStateRepository _sourceState;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly TimeProvider _clock;
    private readonly ILogger<HostRateLimiter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostRateLimiter"/> class.
    /// </summary>
    /// <param name="sourceState">Repository for daily-budget and backoff persistence.</param>
    /// <param name="configProvider">Configuration accessor seam.</param>
    /// <param name="clock">Time provider (injectable for tests).</param>
    /// <param name="logger">Logger.</param>
    public HostRateLimiter(
        SourceStateRepository sourceState,
        IPluginConfigurationProvider configProvider,
        TimeProvider clock,
        ILogger<HostRateLimiter> logger)
    {
        _sourceState  = sourceState;
        _configProvider = configProvider;
        _clock        = clock;
        _logger       = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Acquires a rate-limit token for <paramref name="sourceId"/>.
    /// Waits until a token is available, then checks daily budget and the persisted backoff floor.
    /// </summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="DailyBudgetExhaustedException">When the daily budget is exhausted.</exception>
    public async Task AcquireAsync(string sourceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Check persisted backoff floor from previous Retry-After headers.
        var state = await _sourceState.GetAsync(sourceId, ct).ConfigureAwait(false);
        if (state?.NextAllowedAt is { } floor && floor > _clock.GetUtcNow())
        {
            TimeSpan delay = floor - _clock.GetUtcNow();
            _logger.LogDebug(
                "Source {Source}: honoring backoff floor — waiting {Delay:g}.",
                sourceId, delay);
            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
        }

        // Check daily budget when a finite budget is configured.
        var cfg = GetConfigFor(sourceId);
        if (cfg.RequestsPerDay.HasValue)
        {
            int allowance = await _sourceState
                .GetAllowanceAsync(sourceId, cfg.RequestsPerDay.Value, ct)
                .ConfigureAwait(false);

            if (allowance <= 0)
            {
                _logger.LogWarning(
                    "Source {Source}: daily budget of {Budget} calls exhausted.",
                    sourceId, cfg.RequestsPerDay.Value);
                throw new DailyBudgetExhaustedException(sourceId);
            }
        }

        // Wait for a token from the per-source token-bucket limiter.
        var limiter = GetOrCreateLimiter(sourceId, cfg);
        using var lease = await limiter.AcquireAsync(permitCount: 1, ct).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            // Should not happen with TokenBucketRateLimiter + AcquireAsync, but guard anyway.
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException($"Rate-limit token for '{sourceId}' could not be acquired.");
        }
    }

    /// <summary>
    /// Records a Retry-After backoff for <paramref name="sourceId"/>: persists
    /// <paramref name="until"/> to <c>source_state.next_allowed_at</c> so it survives restarts.
    /// </summary>
    public async Task SetBackoffAsync(string sourceId, DateTimeOffset until, CancellationToken ct)
    {
        _logger.LogInformation(
            "Source {Source}: setting backoff floor to {Until:O} (from Retry-After).",
            sourceId, until);
        await _sourceState.SetNextAllowedAtAsync(sourceId, until, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Increments the daily call counter for <paramref name="sourceId"/> in the DB.
    /// Call this after every successful HTTP request.
    /// </summary>
    public Task RecordCallAsync(string sourceId, CancellationToken ct)
        => _sourceState.IncrementCallCounterAsync(sourceId, ct);

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var limiter in _limiters.Values)
            await limiter.DisposeAsync().ConfigureAwait(false);

        _limiters.Clear();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private TokenBucketRateLimiter GetOrCreateLimiter(string sourceId, RateLimitConfig cfg)
        => _limiters.GetOrAdd(sourceId, _ => BuildLimiter(cfg));

    private static TokenBucketRateLimiter BuildLimiter(RateLimitConfig cfg)
    {
        // tokensPerPeriod and tokenLimit are both ceil(RequestsPerSecond), minimum 1.
        int tokens = Math.Max(1, (int)Math.Ceiling(cfg.RequestsPerSecond));
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit           = tokens,
            TokensPerPeriod      = tokens,
            ReplenishmentPeriod  = TimeSpan.FromSeconds(1),
            AutoReplenishment    = true,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit           = int.MaxValue,
        });
    }

    private RateLimitConfig GetConfigFor(string sourceId)
    {
        var rateLimits = _configProvider.GetConfiguration()?.RateLimits;
        if (rateLimits is not null)
        {
            foreach (var entry in rateLimits)
            {
                if (string.Equals(entry.Source, sourceId, StringComparison.OrdinalIgnoreCase))
                    return entry.Config;
            }
        }

        return DefaultLimits.TryGetValue(sourceId, out var def)
            ? def
            : new RateLimitConfig { RequestsPerSecond = 1 };
    }
}
