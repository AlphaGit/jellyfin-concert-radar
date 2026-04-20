using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ConcertRadar.Api;

/// <summary>
/// Serves the user-facing concerts HTML fragment consumed by the Plugin Pages
/// (IAmParadox27) integration. Plugin Pages fetches this URL via the main
/// Jellyfin SPA and injects the response into the user preferences shell, so
/// the response must be an HTML fragment (no &lt;html&gt;/&lt;body&gt; shell)
/// with its own inline script that self-initializes on insertion.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/ConcertRadar/UserView")]
public sealed class UserViewController : ControllerBase
{
    private const string ResourceName = "Jellyfin.Plugin.ConcertRadar.Web.user-view.html";

    /// <summary>
    /// Returns the embedded HTML fragment for the user-facing concerts listing.
    /// </summary>
    /// <returns>HTML stream.</returns>
    [HttpGet]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetUserView()
    {
        var assembly = typeof(UserViewController).Assembly;
        var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return NotFound();
        }
        return File(stream, "text/html; charset=utf-8");
    }
}
