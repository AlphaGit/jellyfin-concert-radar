using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.ScheduledTasks;
using Jellyfin.Plugin.ConcertRadar.Sources;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Api;

/// <summary>
/// Administrative API endpoints requiring elevated (admin) privileges.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/ConcertRadar/api/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly ConcertRepository _concertRepository;
    private readonly ArtistRepository _artistRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly ITaskManager _taskManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AdminController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="concertRepository">Concert repository.</param>
    /// <param name="artistRepository">Artist repository.</param>
    /// <param name="sourceStateRepository">Source state repository.</param>
    /// <param name="taskManager">Jellyfin task manager.</param>
    /// <param name="httpClientFactory">Named HttpClient factory.</param>
    /// <param name="logger">Logger.</param>
    public AdminController(
        ConcertRepository concertRepository,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        ITaskManager taskManager,
        IHttpClientFactory httpClientFactory,
        ILogger<AdminController> logger)
    {
        _concertRepository      = concertRepository;
        _artistRepository       = artistRepository;
        _sourceStateRepository  = sourceStateRepository;
        _taskManager            = taskManager;
        _httpClientFactory      = httpClientFactory;
        _logger                 = logger;
    }

    /// <summary>
    /// Triggers the <see cref="RefreshConcertsTask"/> immediately.
    /// </summary>
    /// <returns>202 Accepted.</returns>
    [HttpPost("run-now")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RunNow()
    {
        // ITaskManager.QueueScheduledTask<T> is the canonical way to trigger a task.
        // It is fire-and-forget — returns immediately and Jellyfin runs the task in the background.
        try
        {
            _taskManager.QueueScheduledTask<RefreshConcertsTask>();
            _logger.LogInformation("AdminController: RefreshConcertsTask queued via run-now.");
            return Accepted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AdminController: failed to queue RefreshConcertsTask.");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to queue the refresh task. See server logs for details.");
        }
    }

    /// <summary>
    /// Clears the circuit breaker for a specific source.
    /// </summary>
    /// <param name="id">The source identifier (e.g. "ticketmaster").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 No Content.</returns>
    [HttpPost("sources/{id}/reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> ResetSourceAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!KnownSources.Ids.Contains(id))
            return BadRequest($"Unknown source id '{id}'.");

        await _sourceStateRepository.ResetAsync(id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("AdminController: circuit breaker reset for source '{Source}'.", id);
        return NoContent();
    }

    /// <summary>
    /// Deletes all concert records from the database (forces a full re-fetch on the next run).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with the number of deleted rows.</returns>
    [HttpPost("purge")]
    [ProducesResponseType(typeof(PurgeResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<PurgeResult>> PurgeAllAsync(
        CancellationToken cancellationToken = default)
    {
        int deleted = await _concertRepository.PurgeAllAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogWarning("AdminController: purged {Count} concert record(s).", deleted);
        return Ok(new PurgeResult(deleted));
    }

    /// <summary>
    /// Clears all cached external IDs (<c>ext_ids</c>) for every artist, forcing the resolver
    /// to re-query MusicBrainz on the next scheduler run.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 No Content.</returns>
    [HttpPost("resolve-ids")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ResolveIdsAsync(
        CancellationToken cancellationToken = default)
    {
        await _artistRepository.ClearAllExternalIdsAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("AdminController: cleared all artist external IDs for re-resolution.");
        return NoContent();
    }

    /// <summary>Result payload for the purge endpoint.</summary>
    public sealed record PurgeResult(int Deleted);

    /// <summary>
    /// Proxies a Nominatim geocoding query. The admin page cannot call Nominatim
    /// directly because the Jellyfin web client runs on a different origin and
    /// Nominatim does not emit CORS headers; performing the request server-side
    /// also lets us attach a compliant <c>User-Agent</c> per Nominatim's usage
    /// policy.
    /// </summary>
    /// <param name="q">Free-text location query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON array passthrough from Nominatim (or an error payload).</returns>
    [HttpGet("geocode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult> GeocodeAsync(
        [FromQuery] string? q,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest("Missing q.");
        if (q.Length > 200)               return BadRequest("Query too long.");

        var url = "https://nominatim.openstreetmap.org/search"
                + "?q=" + Uri.EscapeDataString(q)
                + "&format=jsonv2&addressdetails=1&limit=5";

        try
        {
            using var client = _httpClientFactory.CreateClient(PluginHttpClient.ClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Nominatim requires a distinctive User-Agent identifying the application.
            req.Headers.UserAgent.ParseAdd("JellyfinConcertRadar/0.1 (+https://github.com/alphagit/jellyfin-concert-radar)");
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("AdminController: Nominatim returned {Status} for geocode query.", resp.StatusCode);
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Upstream " + (int)resp.StatusCode });
            }
            return Content(body, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AdminController: geocode request failed.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Geocoding request failed." });
        }
    }
}
