using System.Reflection;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.ConcertRadar.Api;
using Jellyfin.Plugin.ConcertRadar.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.ConcertRadar.Tests.Api;

/// <summary>
/// The plugin's HTTP surface follows the host's own convention (<c>docs/http-surface.md</c>):
/// PascalCase route segments with no <c>api</c> segment, and every JSON response states its
/// naming instead of inheriting the host default. Jellyfin 12 changed that default from camelCase
/// to PascalCase, which is why this is a rule and not an accident.
/// </summary>
public class HttpSurfaceTests
{
    private const string Base = "Plugins/ConcertRadar";

    // ---- The routes themselves ----

    [Fact]
    public void ThePluginServesExactlyTheseRoutes()
    {
        var expected = new[]
        {
            "GET Plugins/ConcertRadar/Artists",
            "GET Plugins/ConcertRadar/Concerts",
            "GET Plugins/ConcertRadar/Status",
            "GET Plugins/ConcertRadar/Admin/Geocode",
            "POST Plugins/ConcertRadar/Admin/Purge",
            "POST Plugins/ConcertRadar/Admin/ResolveIds",
            "POST Plugins/ConcertRadar/Admin/RunNow",
            "POST Plugins/ConcertRadar/Admin/Sources/{id}/Reset",
            "GET Plugins/ConcertRadar/UserView",
        };

        Assert.Equal(
            expected.OrderBy(r => r, StringComparer.Ordinal),
            PluginControllers().SelectMany(RoutesOf).OrderBy(r => r, StringComparer.Ordinal));
    }

    // ---- Rule 1: route segments are PascalCase, concatenated, with no api segment ----

    [Theory]
    [InlineData(true, "Concerts")]
    [InlineData(true, "RunNow")]
    [InlineData(true, "UserView")]
    [InlineData(true, "{id}")] // a route parameter is not a name the convention governs
    [InlineData(false, "concerts")]
    [InlineData(false, "run-now")]
    [InlineData(false, "api")]
    [InlineData(false, "Api")]
    [InlineData(false, "resolve_ids")]
    public void TheCasingRule_AcceptsOnlyPascalCaseSegmentsAndNoApiSegment(bool expected, string segment)
    {
        Assert.Equal(expected, IsConventionalSegment(segment));
    }

    [Fact]
    public void EveryRouteThePluginServes_UsesPascalCaseSegmentsAndNoApiSegment()
    {
        var offenders = PluginControllers()
            .SelectMany(RoutesOf)
            .Where(route => route.Split(' ')[1].Split('/').Any(segment => !IsConventionalSegment(segment)))
            .ToList();

        Assert.True(offenders.Count == 0, string.Join("  |  ", offenders));
    }

    // ---- Rule 2: every endpoint that returns a body declares its field naming ----

    [Theory]
    [InlineData(true, JsonDefaults.CamelCaseMediaType)]
    [InlineData(true, "text/html")]
    [InlineData(false)] // declares nothing, so it inherits whatever the host does: the defect
    [InlineData(false, "application/json")]
    [InlineData(false, JsonDefaults.PascalCaseMediaType)]
    [InlineData(false, "text/html", "application/json")]
    public void TheNamingRule_AcceptsOnlyJsonThatCarriesTheCamelCaseProfile(bool expected, params string[] produced)
    {
        Assert.Equal(expected, DeclaresItsNaming(produced));
    }

    [Fact]
    public void EveryActionThatReturnsJson_DeclaresTheCamelCaseProfile()
    {
        var offenders = PluginActions()
            .Where(action => !DeclaresItsNaming(ProducedContentTypes(action)))
            .Select(action => action.DeclaringType!.Name + "." + action.Name)
            .ToList();

        Assert.True(offenders.Count == 0, string.Join("  |  ", offenders));
    }

    /// <summary>
    /// The response a page receives, serialized with the options the endpoint's own declaration
    /// selects — never <c>CamelCaseOptions</c> directly, which would pass against an endpoint that
    /// declares nothing.
    /// </summary>
    [Fact]
    public void TheConcertsList_AsItsEndpointDeclaresIt_IsCamelCase()
    {
        var action = typeof(ConcertsController).GetMethod(nameof(ConcertsController.GetConcertsAsync))!;
        var options = ProducedContentTypes(action).Contains(JsonDefaults.CamelCaseMediaType)
            ? JsonDefaults.CamelCaseOptions
            : JsonDefaults.Options;

        var json = JsonSerializer.Serialize(new QueryResult<int>(new[] { 1 }, 1), options);

        Assert.Equal("""{"items":[1],"total":1}""", json);
    }

    // ---- The pages call what the plugin serves ----

    [Theory]
    [InlineData("admin.html")]
    [InlineData("view.html")]
    [InlineData("user-view.html")]
    public void NoEmbeddedPage_CallsTheRetiredApiPrefix(string page)
    {
        var resource = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.ConcertRadar.Web." + page)!;
        using var reader = new StreamReader(resource);
        var text = reader.ReadToEnd();

        Assert.DoesNotContain(Base + "/api", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'" + Base + "'", text, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static bool IsConventionalSegment(string segment)
        => segment.StartsWith('{')
           || (segment.Length > 0
               && char.IsAsciiLetterUpper(segment[0])
               && segment.All(char.IsAsciiLetterOrDigit)
               && !segment.Equals("api", StringComparison.OrdinalIgnoreCase));

    private static bool DeclaresItsNaming(IReadOnlyList<string> produced)
        => produced.Count > 0
           && (produced.Contains(JsonDefaults.CamelCaseMediaType, StringComparer.OrdinalIgnoreCase)
               || !produced.Any(type => type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The action's own <c>[Produces]</c> if present, otherwise its controller's.</summary>
    private static IReadOnlyList<string> ProducedContentTypes(MethodInfo action)
    {
        var own = action.GetCustomAttributes<ProducesAttribute>().SelectMany(a => a.ContentTypes).ToList();
        return own.Count > 0
            ? own
            : action.DeclaringType!.GetCustomAttributes<ProducesAttribute>().SelectMany(a => a.ContentTypes).ToList();
    }

    private static IEnumerable<Type> PluginControllers()
        => typeof(Plugin).Assembly.GetTypes().Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

    private static IEnumerable<MethodInfo> PluginActions()
        => PluginControllers().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(action => action.GetCustomAttributes<HttpMethodAttribute>().Any());

    /// <summary>Every action's full route, as <c>"GET Plugins/ConcertRadar/Concerts"</c>.</summary>
    private static IEnumerable<string> RoutesOf(Type controller)
    {
        var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template
            ?? throw new InvalidOperationException($"{controller.Name} declares no [Route].");

        foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var http in action.GetCustomAttributes<HttpMethodAttribute>())
            {
                var template = string.IsNullOrEmpty(http.Template) ? prefix : prefix + "/" + http.Template;
                yield return string.Join('/', http.HttpMethods) + " " + template;
            }
        }
    }
}
