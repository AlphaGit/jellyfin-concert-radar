using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Controllable <see cref="TimeProvider"/> for deterministic time-sensitive tests.
/// Overrides <see cref="CreateTimer"/> so that <c>Task.Delay(duration, stubClock, ct)</c>
/// completes as soon as <see cref="Set"/> or <see cref="Advance"/> moves the stub clock
/// past the delay deadline — no real wall-clock waiting required.
/// </summary>
internal sealed class TimeProviderStub : TimeProvider
{
    private DateTimeOffset _utcNow;
    private readonly List<FakeTimer> _timers = new();
    private readonly object _lock = new();

    public TimeProviderStub(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    /// <summary>Advances or rewinds the stub clock to the given instant and fires any due timers.</summary>
    public void Set(DateTimeOffset utcNow)
    {
        List<FakeTimer> due;
        lock (_lock)
        {
            _utcNow = utcNow;
            due = CollectDueTimers();
        }

        FireTimers(due);
    }

    /// <summary>Advances the clock by the given duration and fires any due timers.</summary>
    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> due;
        lock (_lock)
        {
            _utcNow = _utcNow.Add(delta);
            due = CollectDueTimers();
        }

        FireTimers(due);
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return _utcNow;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns a <see cref="FakeTimer"/> that fires its callback when the stub clock is
    /// advanced past the timer's due time via <see cref="Set"/> or <see cref="Advance"/>.
    /// Timers with <see cref="Timeout.InfiniteTimeSpan"/> due-time never fire automatically.
    /// </remarks>
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        DateTimeOffset dueAt;
        lock (_lock)
        {
            dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : _utcNow.Add(dueTime);
        }

        var timer = new FakeTimer(callback, state, dueAt, period, this);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        // Fire immediately if dueTime is zero.
        if (dueTime == TimeSpan.Zero)
        {
            timer.FireCallback();
        }

        return timer;
    }

    internal void RemoveTimer(FakeTimer timer)
    {
        lock (_lock)
            _timers.Remove(timer);
    }

    // Must be called while _lock is held.
    private List<FakeTimer> CollectDueTimers()
    {
        var due = new List<FakeTimer>();
        foreach (var t in _timers)
        {
            if (t.DueAt <= _utcNow)
                due.Add(t);
        }

        return due;
    }

    private static void FireTimers(List<FakeTimer> due)
    {
        foreach (var t in due)
            t.FireCallback();
    }

    // ── Nested fake timer ─────────────────────────────────────────────────────

    internal sealed class FakeTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly TimeProviderStub _owner;
        private DateTimeOffset _dueAt;
        private bool _disposed;

        internal FakeTimer(
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt,
            TimeSpan period,    // period not used — Task.Delay always passes Infinite
            TimeProviderStub owner)
        {
            _callback = callback;
            _state    = state;
            _dueAt    = dueAt;
            _owner    = owner;
            _ = period; // suppress unused-parameter warning
        }

        internal DateTimeOffset DueAt => _dueAt;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
                return false;

            lock (_owner._lock)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : _owner._utcNow.Add(dueTime);
            }

            return true;
        }

        internal void FireCallback()
        {
            if (!_disposed)
                _callback(_state);
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.RemoveTimer(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
