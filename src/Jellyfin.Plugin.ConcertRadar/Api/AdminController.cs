using System;
using System.Linq;
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
    private readonly ILogger<AdminController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="concertRepository">Concert repository.</param>
    /// <param name="artistRepository">Artist repository.</param>
    /// <param name="sourceStateRepository">Source state repository.</param>
    /// <param name="taskManager">Jellyfin task manager.</param>
    /// <param name="logger">Logger.</param>
    public AdminController(
        ConcertRepository concertRepository,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        ITaskManager taskManager,
        ILogger<AdminController> logger)
    {
        _concertRepository      = concertRepository;
        _artistRepository       = artistRepository;
        _sourceStateRepository  = sourceStateRepository;
        _taskManager            = taskManager;
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
}
