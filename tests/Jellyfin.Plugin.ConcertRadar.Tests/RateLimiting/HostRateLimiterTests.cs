using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.RateLimiting;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.RateLimiting;

/// <summary>
/// Integration tests for <see cref="HostRateLimiter"/> using a real SQLite database
/// (via <see cref="TestDatabase"/>) and a controllable <see cref="TimeProviderStub"/>.
/// </summary>
public class HostRateLimiterTests
{
    // Use a known source id — T14.8 guards the limiter against unknown ids.
    private const string Source = "ticketmaster";

    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2025, 6, 15, 14, 0, 0, TimeSpan.Zero); // 14:00 UTC — mid-day

    // ── Helper: builds a HostRateLimiter wired to a test DB ──────────────────

    private static async Task<(HostRateLimiter limiter, TestDatabase db)> BuildAsync(
        TimeProvider clock,
        PluginConfiguration? cfg = null)
    {
        var db = await TestDatabase.CreateAsync(clock);
        var configProvider = new StubPluginConfigurationProvider(cfg ?? new PluginConfiguration());
        var limiter = new HostRateLimiter(
            db.SourceState,
            configProvider,
            clock,
            NullLogger<HostRateLimiter>.Instance);
        return (limiter, db);
    }

    // ── Token-bucket throughput ───────────────────────────────────────────────

    [Fact]
    public async Task AllowsUpToRequestsPerSecond()
    {
        // Configure 5 req/s via the PluginConfiguration.
        var cfg = new PluginConfiguration
        {
            RateLimits = new List<SourceRateLimitEntry>
            {
                new() { Source = Source, Config = new RateLimitConfig { RequestsPerSecond = 5 } },
            },
        };
        var clock = new TimeProviderStub(BaseTime);
        var (limiter, db) = await BuildAsync(clock, cfg);
        await using (db)
        await using (limiter)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var sw = Stopwatch.StartNew();
            // 5 acquires should complete immediately (bucket starts full).
            for (int i = 0; i < 5; i++)
                await limiter.AcquireAsync(Source, cts.Token);
            sw.Stop();

            // All 5 tokens consumed from a full bucket — should be well under 1 second.
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
                because: "5 req/s bucket should grant all 5 tokens immediately on a cold start");
        }
    }

    [Fact]
    public async Task ThrottlesBeyondRequestsPerSecond()
    {
        // 1 req/s so the 2nd acquire must wait for replenishment.
        var cfg = new PluginConfiguration
        {
            RateLimits = new List<SourceRateLimitEntry>
            {
                new() { Source = Source, Config = new RateLimitConfig { RequestsPerSecond = 1 } },
            },
        };
        var clock = new TimeProviderStub(BaseTime);
        var (limiter, db) = await BuildAsync(clock, cfg);
        await using (db)
        await using (limiter)
        {
            // 1.5s timeout — the 2nd token should arrive within the replenishment window.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

            // First acquire — immediately granted.
            await limiter.AcquireAsync(Source, cts.Token);

            // Second acquire — must wait for the bucket to replenish (≥ ~1s with 1 token/s).
            var sw = Stopwatch.StartNew();
            await limiter.AcquireAsync(Source, cts.Token);
            sw.Stop();

            sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800),
                because: "the 2nd token for a 1 req/s bucket cannot be granted until replenishment (~1s)");
        }
    }

    // ── Daily budget ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DailyBudgetExhausted_ThrowsDailyBudgetExhaustedException()
    {
        // Budget of 3 calls on this source.
        var cfg = new PluginConfiguration
        {
            RateLimits = new List<SourceRateLimitEntry>
            {
                new()
                {
                    Source = Source,
                    Config = new RateLimitConfig
                    {
                        RequestsPerSecond = 100, // don't throttle
                        RequestsPerDay    = 3,
                    },
                },
            },
        };
        var clock = new TimeProviderStub(BaseTime);
        var (limiter, db) = await BuildAsync(clock, cfg);
        await using (db)
        await using (limiter)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Exhaust the budget by recording 3 calls directly against the state repo
            // (simulating successful HTTP calls), then attempt a 4th acquire.
            for (int i = 0; i < 3; i++)
                await db.SourceState.IncrementCallCounterAsync(Source, cts.Token);

            // 4th acquire should raise DailyBudgetExhaustedException.
            var act = async () => await limiter.AcquireAsync(Source, cts.Token);

            await act.Should().ThrowAsync<DailyBudgetExhaustedException>()
                .WithMessage($"*'{Source}'*");
        }
    }

    [Fact]
    public async Task DailyBudget_ResetsAtMidnightUtc()
    {
        var cfg = new PluginConfiguration
        {
            RateLimits = new List<SourceRateLimitEntry>
            {
                new()
                {
                    Source = Source,
                    Config = new RateLimitConfig
                    {
                        RequestsPerSecond = 100,
                        RequestsPerDay    = 2,
                    },
                },
            },
        };

        // Start at 23:55 UTC on day 1.
        var day1Evening = new DateTimeOffset(2025, 6, 15, 23, 55, 0, TimeSpan.Zero);
        var clock = new TimeProviderStub(day1Evening);
        var (limiter, db) = await BuildAsync(clock, cfg);
        await using (db)
        await using (limiter)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Exhaust budget on day 1.
            for (int i = 0; i < 2; i++)
                await db.SourceState.IncrementCallCounterAsync(Source, cts.Token);

            // Advance clock past midnight.
            clock.Set(new DateTimeOffset(2025, 6, 16, 0, 5, 0, TimeSpan.Zero));

            // Budget should be reset — acquire must succeed without throwing.
            var act = async () => await limiter.AcquireAsync(Source, cts.Token);
            await act.Should().NotThrowAsync(
                because: "budget resets at midnight UTC so the first call on day 2 must succeed");
        }
    }

    // ── Backoff / Retry-After ─────────────────────────────────────────────────

    [Fact]
    public async Task SetBackoffAsync_HonorsBackoff_UntilPassed()
    {
        var cfg = new PluginConfiguration
        {
            RateLimits = new List<SourceRateLimitEntry>
            {
                new() { Source = Source, Config = new RateLimitConfig { RequestsPerSecond = 100 } },
            },
        };
        var clock = new TimeProviderStub(BaseTime);
        var (limiter, db) = await BuildAsync(clock, cfg);
        await using (db)
        await using (limiter)
        {
            // Set a backoff floor 30 seconds of stub-time from now.
            // Using stub time means we never actually wait 30 seconds — the clock is
            // advanced programmatically, which fires the FakeTimer registered inside
            // Task.Delay(delay, _clock, ct) inside AcquireAsync.
            var backoffUntil = BaseTime.AddSeconds(30);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await limiter.SetBackoffAsync(Source, backoffUntil, cts.Token);

            // Start AcquireAsync in the background — it will enter Task.Delay waiting for
            // the backoff floor to pass.  Give it a moment to reach the delay and register
            // its FakeTimer with the stub clock (one real async round-trip is enough).
            var acquireTask = Task.Run(() => limiter.AcquireAsync(Source, cts.Token), cts.Token);

            // Wait for AcquireAsync to register the timer with the stub clock.
            // A short real delay is still needed here — but 500 ms is ample even on
            // the most loaded CI runner (the only work before the timer is a DB read).
            await Task.Delay(500, CancellationToken.None);

            // Advance stub clock past the floor: TimeProviderStub.CreateTimer fires all
            // due FakeTimers synchronously, which unblocks Task.Delay inside AcquireAsync.
            clock.Set(backoffUntil.AddMilliseconds(10));

            // AcquireAsync must complete promptly once the timer fired.
            await acquireTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

            // Test passes if no OperationCanceledException or other exception was thrown.
        }
    }
}
