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

        // Resolve MBID → Jellyfin artist GUID so the UI can link each concert's
        // artist name to the local artist page.
        var mbids = result.Items
            .Select(r => r.ArtistMbid)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyDictionary<string, Guid> jellyfinMap = mbids.Count == 0
            ? new Dictionary<string, Guid>()
            : await _artistRepository.GetJellyfinIdsByMbidsAsync(mbids, cancellationToken)
                .ConfigureAwait(false);

        var dtos = result.Items.Select(r =>
        {
            Guid? jid = null;
            if (!string.IsNullOrWhiteSpace(r.ArtistMbid)
                && jellyfinMap.TryGetValue(r.ArtistMbid, out var g))
            {
                jid = g;
            }
            return ConcertDto.FromRecord(r, jid);
        }).ToList();
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
        var triggersDebug = new List<TriggerDebugDto>();

        try
        {
            var worker = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(
                    t.ScheduledTask.Key, "ConcertRadar.Refresh",
                    StringComparison.OrdinalIgnoreCase));

            if (worker is not null)
            {
                lastRun = worker.LastExecutionResult?.EndTimeUtc;

                // Jellyfin DailyTrigger / WeeklyTrigger interpret TimeOfDayTicks as SERVER
                // LOCAL time (they compare against DateTime.Now). Anchor the computation to
                // local time so the UI shows what Jellyfin will actually fire.
                nextRun = ComputeNextRun(worker.Triggers, DateTimeOffset.Now);

                if (worker.Triggers is not null)
                {
                    foreach (var t in worker.Triggers)
                    {
                        triggersDebug.Add(new TriggerDebugDto(
                            Type:          t.Type.ToString(),
                            TimeOfDay:     t.TimeOfDayTicks.HasValue
                                               ? TimeSpan.FromTicks(t.TimeOfDayTicks.Value).ToString(@"hh\:mm")
                                               : null,
                            DayOfWeek:     t.DayOfWeek?.ToString(),
                            IntervalHours: t.IntervalTicks.HasValue
                                               ? TimeSpan.FromTicks(t.IntervalTicks.Value).TotalHours
                                               : null));
                    }
                }

                _logger.LogInformation(
                    "ConcertsController: task '{Key}' has {Count} trigger(s). LastRun={LastRun}, NextRun={NextRun} (server local now={Now}, tz={Tz}).",
                    worker.ScheduledTask.Key, triggersDebug.Count, lastRun, nextRun,
                    DateTimeOffset.Now, TimeZoneInfo.Local.Id);
            }
            else
            {
                _logger.LogWarning("ConcertsController: scheduled task 'ConcertRadar.Refresh' not found in ITaskManager.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConcertsController: could not resolve task schedule info.");
        }

        // Strip LastError from the user-facing DTO: error strings may reveal internal endpoints
        // or failure modes even after UrlRedactor processing. Admins can check the Jellyfin log.
        var response = new StatusResponse(
            Sources:       sourceStates.Select(r => SourceStatusDto.FromRecord(r) with { LastError = null }).ToList(),
            LastRun:       lastRun,
            NextRun:       nextRun,
            QueueSize:     queueSize,
            ServerNow:     DateTimeOffset.Now,
            ServerTimeZone: TimeZoneInfo.Local.Id,
            Triggers:      triggersDebug);

        return Ok(response);
    }

    /// <summary>Status response returned by GET /status.</summary>
    public sealed record StatusResponse(
        IReadOnlyList<SourceStatusDto> Sources,
        DateTimeOffset? LastRun,
        DateTimeOffset? NextRun,
        int QueueSize,
        DateTimeOffset ServerNow,
        string ServerTimeZone,
        IReadOnlyList<TriggerDebugDto> Triggers);

    /// <summary>Diagnostic snapshot of a single scheduled-task trigger.</summary>
    public sealed record TriggerDebugDto(
        string Type,
        string? TimeOfDay,
        string? DayOfWeek,
        double? IntervalHours);

    private static DateTimeOffset? ComputeNextRun(IEnumerable<TaskTriggerInfo>? triggers, DateTimeOffset localNow)
    {
        if (triggers is null) return null;

        DateTimeOffset? soonest = null;
        foreach (var t in triggers)
        {
            DateTimeOffset? candidate = t.Type switch
            {
                TaskTriggerInfoType.DailyTrigger    => NextDailyFire(localNow, t.TimeOfDayTicks),
                TaskTriggerInfoType.WeeklyTrigger   => NextWeeklyFire(localNow, t.DayOfWeek, t.TimeOfDayTicks),
                TaskTriggerInfoType.IntervalTrigger => t.IntervalTicks.HasValue && t.IntervalTicks.Value > 0
                    ? localNow + TimeSpan.FromTicks(t.IntervalTicks.Value)
                    : null,
                _ => null,
            };
            if (candidate.HasValue && (!soonest.HasValue || candidate.Value < soonest.Value))
            {
                soonest = candidate;
            }
        }
        return soonest;
    }

    // Jellyfin's DailyTrigger.Start() uses DateTime.Now (server local), so anchor here
    // to localNow.DateTime.Date with localNow.Offset — preserves the server's UTC offset.
    private static DateTimeOffset? NextDailyFire(DateTimeOffset localNow, long? timeOfDayTicks)
    {
        if (!timeOfDayTicks.HasValue) return null;
        var tod  = TimeSpan.FromTicks(timeOfDayTicks.Value);
        var next = new DateTimeOffset(localNow.DateTime.Date, localNow.Offset) + tod;
        if (next <= localNow) next = next.AddDays(1);
        return next;
    }

    private static DateTimeOffset? NextWeeklyFire(DateTimeOffset localNow, DayOfWeek? dow, long? timeOfDayTicks)
    {
        if (!dow.HasValue || !timeOfDayTicks.HasValue) return null;
        var tod  = TimeSpan.FromTicks(timeOfDayTicks.Value);
        var next = new DateTimeOffset(localNow.DateTime.Date, localNow.Offset) + tod;
        int delta = ((int)dow.Value - (int)next.DayOfWeek + 7) % 7;
        if (delta == 0 && next <= localNow) delta = 7;
        return next.AddDays(delta);
    }
}
