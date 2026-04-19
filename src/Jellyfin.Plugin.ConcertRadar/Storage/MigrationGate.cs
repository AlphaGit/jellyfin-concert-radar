using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ConcertRadar.Storage;

/// <summary>
/// Default implementation of <see cref="IMigrationGate"/> backed by a
/// <see cref="TaskCompletionSource{T}"/>.  Registered as a singleton so all
/// repositories share the same gate instance.
/// </summary>
public sealed class MigrationGate : IMigrationGate
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public Task WaitAsync(CancellationToken ct)
    {
        if (_tcs.Task.IsCompleted)
            return Task.CompletedTask;

        // Respect cancellation while waiting for the gate.
        return _tcs.Task.WaitAsync(ct);
    }

    /// <inheritdoc />
    public void SignalReady() => _tcs.TrySetResult(true);
}
