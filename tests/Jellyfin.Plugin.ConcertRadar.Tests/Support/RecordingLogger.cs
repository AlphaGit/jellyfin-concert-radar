using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Records what was logged, so a test can assert how often and at what level. Every other test
/// in this suite uses <c>NullLogger</c>; reach for this one only where the logging *is* the
/// specified behaviour, as it is for the optional Plugin Pages integration.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Add((logLevel, formatter(state, exception)));
}
