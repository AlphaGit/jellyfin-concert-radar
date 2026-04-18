using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Support;

/// <summary>
/// Fake <see cref="HttpMessageHandler"/> that returns pre-configured responses keyed by
/// URL regex pattern.  Used to test HTTP adapters without network access.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Regex Pattern, Func<HttpRequestMessage, int, HttpResponseMessage> Factory)> _rules = new();
    private int _callCount;

    /// <summary>
    /// Total number of <see cref="SendAsync"/> invocations received.
    /// </summary>
    public int CallCount => _callCount;

    /// <summary>
    /// All requests received, in order.
    /// </summary>
    public List<HttpRequestMessage> ReceivedRequests { get; } = new();

    /// <summary>
    /// Registers a response for requests whose URL matches <paramref name="urlPattern"/>.
    /// Rules are evaluated in registration order; first match wins.
    /// </summary>
    /// <param name="urlPattern">Regex pattern matched against the full request URL.</param>
    /// <param name="statusCode">HTTP status code to return.</param>
    /// <param name="body">Response body text (UTF-8).</param>
    /// <param name="contentType">MIME type of the body.</param>
    public StubHttpMessageHandler OnUrlPattern(
        string urlPattern,
        HttpStatusCode statusCode,
        string body,
        string contentType = "application/json")
    {
        var re = new Regex(urlPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        _rules.Add((re, (_, _) => BuildResponse(statusCode, body, contentType)));
        return this;
    }

    /// <summary>
    /// Registers a response factory that receives the request and the zero-based attempt index.
    /// Useful for simulating retry sequences (e.g. 429 twice then 200).
    /// </summary>
    public StubHttpMessageHandler OnUrlPattern(
        string urlPattern,
        Func<HttpRequestMessage, int, HttpResponseMessage> factory)
    {
        var re = new Regex(urlPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        _rules.Add((re, factory));
        return this;
    }

    /// <summary>
    /// Registers a fixed response returned for every URL (catch-all rule).
    /// </summary>
    public StubHttpMessageHandler AlwaysReturn(
        HttpStatusCode statusCode,
        string body,
        string contentType = "application/json")
        => OnUrlPattern(".*", statusCode, body, contentType);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref _callCount) - 1;
        ReceivedRequests.Add(request);

        string url = request.RequestUri?.ToString() ?? string.Empty;

        foreach (var (pattern, factory) in _rules)
        {
            if (pattern.IsMatch(url))
                return Task.FromResult(factory(request, attempt));
        }

        // No rule matched — return 404.
        return Task.FromResult(BuildResponse(HttpStatusCode.NotFound, $"No stub rule for: {url}"));
    }

    private static HttpResponseMessage BuildResponse(
        HttpStatusCode statusCode,
        string body,
        string contentType = "application/json")
    {
        var resp = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        return resp;
    }
}
