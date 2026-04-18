using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Jellyfin.Plugin.ConcertRadar.Configuration;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.Sources;
using Jellyfin.Plugin.ConcertRadar.Storage;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Configurable fake <see cref="ISourceAdapter"/> for <see cref="RefreshConcertsTask"/> tests.
/// Records invocations and emits a user-supplied sequence of <see cref="RawEvent"/> per call.
/// Optionally increments the source-state daily counter (to test budget exhaustion).
/// </summary>
internal sealed class FakeSourceAdapter : ISourceAdapter
{
    private readonly Func<ArtistRef, IReadOnlyList<RawEvent>>? _eventsFactory;
    private readonly Exception? _throwOnFetch;
    private readonly SourceStateRepository? _sourceState;
    private int _callCount;

    /// <summary>
    /// Number of times <see cref="FetchAsync"/> was called.
    /// </summary>
    public int CallCount => _callCount;

    /// <summary>
    /// Creates an adapter that returns the given events for every artist.
    /// Optionally pass <paramref name="sourceState"/> to simulate real daily-budget tracking
    /// (each FetchAsync increments the source's <c>calls_today</c> counter).
    /// </summary>
    public FakeSourceAdapter(
        string id = "fake",
        IReadOnlyList<RawEvent>? events = null,
        bool requiresCredentials = false,
        bool requiresTosOptIn = false,
        SourceStateRepository? sourceState = null)
    {
        Id = id;
        DisplayName = id;
        RequiresCredentials = requiresCredentials;
        RequiresTosOptIn = requiresTosOptIn;
        _sourceState = sourceState;

        var fixedEvents = events ?? Array.Empty<RawEvent>();
        _eventsFactory = _ => fixedEvents;
    }

    /// <summary>
    /// Creates an adapter that throws on every fetch call.
    /// </summary>
    public FakeSourceAdapter(string id, Exception throwOnFetch)
    {
        Id = id;
        DisplayName = id;
        RequiresCredentials = false;
        RequiresTosOptIn = false;
        _throwOnFetch = throwOnFetch;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Api;

    /// <inheritdoc />
    public bool RequiresCredentials { get; }

    /// <inheritdoc />
    public bool RequiresTosOptIn { get; }

    /// <inheritdoc />
    public bool IsConfigured(PluginConfiguration cfg) => true;

    /// <inheritdoc />
    public async IAsyncEnumerable<RawEvent> FetchAsync(
        ArtistRef artist,
        SourceFilter filter,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);

        // Simulate real-adapter budget tracking when a repository is provided.
        if (_sourceState is not null)
            await _sourceState.IncrementCallCounterAsync(Id, ct).ConfigureAwait(false);

        if (_throwOnFetch is not null)
            throw _throwOnFetch;

        var events = _eventsFactory!(artist);
        foreach (var ev in events)
        {
            ct.ThrowIfCancellationRequested();
            await System.Threading.Tasks.Task.Yield();
            yield return ev;
        }
    }
}
