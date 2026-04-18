using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ConcertRadar.Api.Dtos;
using Jellyfin.Plugin.ConcertRadar.Model;
using Jellyfin.Plugin.ConcertRadar.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ConcertRadar.Api;

/// <summary>
/// Read-only API endpoints for concert data, accessible to any authenticated user.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/ConcertRadar/api")]
public sealed class ConcertsController : ControllerBase
{
    private readonly ConcertRepository _concertRepository;
    private readonly ArtistRepository _artistRepository;
    private readonly SourceStateRepository _sourceStateRepository;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<ConcertsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcertsController"/> class.
    /// </summary>
    /// <param name="concertRepository">Concert repository.</param>
    /// <param name="artistRepository">Artist repository.</param>
    /// <param name="sourceStateRepository">Source state repository.</param>
    /// <param name="taskManager">Jellyfin task manager.</param>
    /// <param name="logger">Logger.</param>
    public ConcertsController(
        ConcertRepository concertRepository,
        ArtistRepository artistRepository,
        SourceStateRepository sourceStateRepository,
        ITaskManager taskManager,
        ILogger<ConcertsController> logger)
    {
        _concertRepository      = concertRepository;
        _artistRepository       = artistRepository;
        _sourceStateRepository  = sourceStateRepository;
        _taskManager            = taskManager;
        _logger                 = logger;
    }

    /// <summary>
    /// Returns a paginated, filtered list of upcoming concerts.
    /// </summary>
    /// <param name="from">ISO 8601 lower date bound (optional).</param>
    /// <param name="to">ISO 8601 upper date bound (optional).</param>
    /// <param name="country">ISO-3166 alpha-2 country code filter (optional).</param>
    /// <param name="city">City name filter (optional).</param>
    /// <param name="source">Source identifier filter (optional).</param>
    /// <param name="artistMbid">MusicBrainz artist ID filter (optional).</param>
    /// <param name="page">One-based page number (default 1).</param>
    /// <param name="pageSize">Items per page (default 50, max 200).</param>
    /// <param name="sort">Sort field: date | artist | city (default date).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paginated list of concerts.</returns>
    [HttpGet("concerts")]
    [ProducesResponseType(typeof(QueryResult<ConcertDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<ConcertDto>>> GetConcertsAsync(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? country = null,
        [FromQuery] string? city = null,
        [FromQuery] string? source = null,
        [FromQuery] string? artistMbid = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? sort = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 200);

        var sortField = sort?.ToUpperInvariant() switch
        {
            "ARTIST" => ConcertSortField.Artist,
            "CITY"   => ConcertSortField.City,
            _        => ConcertSortField.Date,
        };

        // API uses 1-based pages; repository uses 0-based.
        var query = new ConcertQuery(
            From:       from,
            To:         to,
            Country:    country,
            City:       city,
            Source:     source,
            ArtistMbid: artistMbid,
            Sort:       sortField,
            Page:       page - 1,
            PageSize:   pageSize);

        var result = await _concertRepository.QueryAsync(query, cancellationToken)
            .ConfigureAwait(false);

        var dtos = result.Items.Select(ConcertDto.FromRecord).ToList();
        return Ok(new QueryResult<ConcertDto>(dtos, result.Total));
    }

    /// <summary>
    /// Returns the full list of artists known to the plugin.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of artists with resolution status.</returns>
    [HttpGet("artists")]
    [ProducesResponseType(typeof(QueryResult<ArtistDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<QueryResult<ArtistDto>>> GetArtistsAsync(
        CancellationToken cancellationToken = default)
    {
        var artists = await _artistRepository.GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        var dtos = artists.Select(ArtistDto.FromStored).ToList();
        return Ok(new QueryResult<ArtistDto>(dtos, dtos.Count));
    }

    /// <summary>
    /// Returns the current health status of all sources plus task scheduling info.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Status summary.</returns>
    [HttpGet("status")]
    [ProducesResponseType(typeof(StatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<StatusResponse>> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var sourceStates = await _sourceStateRepository.GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        int queueSize = await _artistRepository.CountUncheckedAsync(cancellationToken)
            .ConfigureAwait(false);

        // Resolve last/next run from ITaskManager.
        DateTimeOffset? lastRun = null;
        DateTimeOffset? nextRun = null;

        // TODO: ITaskManager.ScheduledTasks exposes IScheduledTaskWorker, which has
        // LastExecutionResult and NextScheduledDateTime. The exact surface differs between
        // Jellyfin versions; log a warning and leave null if resolution fails.
        try
        {
            var worker = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(
                    t.ScheduledTask.Key, "ConcertRadar.Refresh",
                    StringComparison.OrdinalIgnoreCase));

            if (worker is not null)
            {
                lastRun = worker.LastExecutionResult?.EndTimeUtc;
                // IScheduledTaskWorker in Jellyfin 10.11.6 does not expose NextScheduledDateTime.
                // nextRun stays null; the admin UI can derive it from Triggers if needed.
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConcertsController: could not resolve task schedule info.");
        }

        var response = new StatusResponse(
            Sources:   sourceStates.Select(SourceStatusDto.FromRecord).ToList(),
            LastRun:   lastRun,
            NextRun:   nextRun,
            QueueSize: queueSize);

        return Ok(response);
    }

    /// <summary>Status response returned by GET /status.</summary>
    public sealed record StatusResponse(
        IReadOnlyList<SourceStatusDto> Sources,
        DateTimeOffset? LastRun,
        DateTimeOffset? NextRun,
        int QueueSize);
}
