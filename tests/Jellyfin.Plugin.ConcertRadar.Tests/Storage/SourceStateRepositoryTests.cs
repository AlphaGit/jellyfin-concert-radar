using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.ConcertRadar.Tests.Support;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Storage;

/// <summary>
/// Integration tests for <see cref="Jellyfin.Plugin.ConcertRadar.Storage.SourceStateRepository"/>.
/// Circuit-breaker and daily-counter logic.
/// </summary>
public class SourceStateRepositoryTests
{
    private const string Source = "ticketmaster";
    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2025, 6, 15, 14, 0, 0, TimeSpan.Zero); // 14:00 UTC, well within a day

    // ── Circuit breaker ───────────────────────────────────────────────────────

    [Fact]
    public async Task RecordFailure_OpensCircuitAfterThreshold()
    {
        var clock = new TimeProviderStub(BaseTime);
        await using var db = await TestDatabase.CreateAsync(clock);
        var repo = db.SourceState;

        const int threshold = 3;
        var cooldown = TimeSpan.FromHours(24);

        for (int i = 0; i < threshold; i++)
            await repo.RecordFailureAsync(Source, "some error", threshold, cooldown, BaseTime, CancellationToken.None);

        var state = await repo.GetAsync(Source, CancellationToken.None);

        state.Should().NotBeNull();
        state!.Status.ToString().Should().Be("Failing",
            because: "after threshold failures the circuit should open");
        state.DisabledUntil.Should().NotBeNull(
            because: "disabled_until must be set when circuit trips");
        state.DisabledUntil!.Value.Should().BeCloseTo(
            BaseTime.Add(cooldown), precision: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RecordSuccess_ClosesCircuitAndResetsCounter()
    {
        var clock = new TimeProviderStub(BaseTime);
        await using var db = await TestDatabase.CreateAsync(clock);
        var repo = db.SourceState;

        // First trip the circuit.
        for (int i = 0; i < 5; i++)
            await repo.RecordFailureAsync(Source, "err", 5, TimeSpan.FromHours(24), BaseTime, CancellationToken.None);

        // Then record a success.
        await repo.RecordSuccessAsync(Source, BaseTime, CancellationToken.None);

        var state = await repo.GetAsync(Source, CancellationToken.None);

        state.Should().NotBeNull();
        state!.ConsecutiveErrors.Should().Be(0,
            because: "RecordSuccess resets the consecutive error counter");
        state.DisabledUntil.Should().BeNull(
            because: "RecordSuccess clears disabled_until");
        state.Status.ToString().Should().Be("Ok");
    }

    [Fact]
    public async Task IsOpen_ReturnsTrueUntilDisabledUntilPasses()
    {
        var clock = new TimeProviderStub(BaseTime);
        await using var db = await TestDatabase.CreateAsync(clock);
        var repo = db.SourceState;

        var cooldown = TimeSpan.FromHours(24);
        // Trip the circuit.
        for (int i = 0; i < 5; i++)
            await repo.RecordFailureAsync(Source, "err", 5, cooldown, BaseTime, CancellationToken.None);

        // Circuit should be open right now.
        bool openNow = await repo.IsOpenAsync(Source, BaseTime, CancellationToken.None);
        openNow.Should().BeTrue(because: "disabled_until is in the future at BaseTime");

        // Advance past the cooldown window.
        DateTimeOffset afterCooldown = BaseTime.Add(cooldown).AddSeconds(1);
        bool openAfter = await repo.IsOpenAsync(Source, afterCooldown, CancellationToken.None);
        openAfter.Should().BeFalse(because: "disabled_until has passed so the circuit should be closed");
    }

    [Fact]
    public async Task Reset_ClearsStateImmediately()
    {
        var clock = new TimeProviderStub(BaseTime);
        await using var db = await TestDatabase.CreateAsync(clock);
        var repo = db.SourceState;

        // Trip the circuit first.
        for (int i = 0; i < 5; i++)
            await repo.RecordFailureAsync(Source, "err", 5, TimeSpan.FromHours(24), BaseTime, CancellationToken.None);

        // Admin resets.
        await repo.ResetAsync(Source, CancellationToken.None);

        var state = await repo.GetAsync(Source, CancellationToken.None);

        state.Should().NotBeNull();
        state!.ConsecutiveErrors.Should().Be(0);
        state.DisabledUntil.Should().BeNull();
        state.Status.ToString().Should().Be("Ok");
        state.CallsToday.Should().Be(0);
        state.LastError.Should().BeNull();
    }

    // ── Daily call counter ────────────────────────────────────────────────────

    [Fact]
    public async Task IncrementCallCounter_ResetsAtMidnightUtc()
    {
        // Set clock to 23:50 on day 1.
        var day1Evening = new DateTimeOffset(2025, 6, 15, 23, 50, 0, TimeSpan.Zero);
        var clock = new TimeProviderStub(day1Evening);
        await using var db = await TestDatabase.CreateAsync(clock);
        var repo = db.SourceState;

        // Make 5 calls on day 1.
        for (int i = 0; i < 5; i++)
            await repo.IncrementCallCounterAsync(Source, CancellationToken.None);

        var beforeMidnight = await repo.GetAsync(Source, CancellationToken.None);
        beforeMidnight!.CallsToday.Should().Be(5,
            because: "all 5 increments happened within the same day");

        // Advance clock past midnight.
        clock.Set(new DateTimeOffset(2025, 6, 16, 0, 5, 0, TimeSpan.Zero));

        // First call on day 2 should reset the counter to 1.
        await repo.IncrementCallCounterAsync(Source, CancellationToken.None);

        var afterMidnight = await repo.GetAsync(Source, CancellationToken.None);
        afterMidnight!.CallsToday.Should().Be(1,
            because: "counter must reset to 1 (the current call) after crossing midnight UTC");
    }

    [Fact]
    public async Task GetAllowance_DecrementsAsCallsIncrement()
    {
        var clock = new TimeProviderStub(BaseTime);
        await using var db = await TestDatabase.CreateAsync(clock);
        var repo = db.SourceState;

        const int budget = 10;

        // No row yet — allowance equals full budget.
        int initial = await repo.GetAllowanceAsync(Source, budget, CancellationToken.None);
        initial.Should().Be(budget, because: "no calls have been made yet");

        // Consume 3 calls.
        for (int i = 0; i < 3; i++)
            await repo.IncrementCallCounterAsync(Source, CancellationToken.None);

        int remaining = await repo.GetAllowanceAsync(Source, budget, CancellationToken.None);
        remaining.Should().Be(budget - 3,
            because: "3 calls were consumed so allowance must decrease by 3");
    }
}
