# The plugin's HTTP surface

Read this before adding an endpoint. Three rules, one exceptions table, and a test behind each.

**The standard is the host's own.** The plugin's endpoints are named as Jellyfin names its
first-party endpoints, so they read as part of the server rather than as a visitor on it.

Why this exists: Jellyfin 12 changed the default JSON naming of plugin responses from camelCase
(10.11) to PascalCase. A plugin that never states its naming inherits whatever the host does.
The rules below make the naming and the routes the plugin's own contract.

---

## Rule 1 — Route segments are PascalCase, concatenated, with no `api` segment

Every segment starts with a capital. A multi-word segment is one word with no separator. There is
no version segment and no `api` segment, because no Jellyfin route has one — compare the host's own
`/ScheduledTasks`, `/QuickConnect/Initiate`, `/Library/VirtualFolders`.

```text
Plugins/ConcertRadar/Concerts            not  .../api/concerts
Plugins/ConcertRadar/Admin/RunNow        not  .../api/admin/run-now
```

Enforced by `HttpSurfaceTests.EveryRouteThePluginServes_UsesPascalCaseSegmentsAndNoApiSegment`,
over a `[Theory]` table of accepting and rejecting segments.

## Rule 2 — Every endpoint that returns a body declares its field naming

A controller that returns JSON carries:

```csharp
[Produces(JsonDefaults.CamelCaseMediaType)]   // Jellyfin.Extensions.Json
```

`JsonDefaults.CamelCaseMediaType` is `application/json; profile="CamelCase"`. Jellyfin offers every
response in either naming and picks by what the caller asks for. Without the attribute the plugin
gets the host default, which is PascalCase on Jellyfin 12.

Returned object fields are therefore camelCase, at every level of nesting. The pages still read
both casings through their `pick()` helper, because Jellyfin's own plugin-configuration endpoint
answers in PascalCase.

**The rule a test enforces:** for every action, the effective produced content types — the
action's `[Produces]` if present, otherwise its controller's — MUST be **non-empty**, and MUST
either contain `JsonDefaults.CamelCaseMediaType` or contain no `application/json` type at all. An
endpoint that declares nothing inherits the host default, which is the defect.

Enforced by `HttpSurfaceTests.EveryActionThatReturnsJson_DeclaresTheCamelCaseProfile` and
`HttpSurfaceTests.TheConcertsList_AsItsEndpointDeclaresIt_IsCamelCase`.

## Rule 3 — Pages build paths from one base, through the host's relative-URL facility

Each embedded page holds one `API_BASE = 'Plugins/ConcertRadar'` literal and builds every request
with `ApiClient.getUrl(API_BASE + path)`. No page writes a server address, a scheme, or the
retired `api` prefix.

Enforced by `HttpSurfaceTests.NoEmbeddedPage_CallsTheRetiredApiPrefix`.

## The route surface

| Method | Route | Controller | Body |
| --- | --- | --- | --- |
| `GET` | `Plugins/ConcertRadar/Concerts` | `ConcertsController` | `QueryResult<ConcertDto>` |
| `GET` | `Plugins/ConcertRadar/Artists` | `ConcertsController` | `QueryResult<ArtistDto>` |
| `GET` | `Plugins/ConcertRadar/Status` | `ConcertsController` | `StatusResponse` |
| `POST` | `Plugins/ConcertRadar/Admin/RunNow` | `AdminController` | none (202) |
| `POST` | `Plugins/ConcertRadar/Admin/Sources/{id}/Reset` | `AdminController` | none |
| `POST` | `Plugins/ConcertRadar/Admin/Purge` | `AdminController` | none |
| `POST` | `Plugins/ConcertRadar/Admin/ResolveIds` | `AdminController` | none (204) |
| `GET` | `Plugins/ConcertRadar/Admin/Geocode?q=` | `AdminController` | Nominatim results |
| `GET` | `Plugins/ConcertRadar/UserView` | `UserViewController` | `text/html` fragment |

`HttpSurfaceTests.ThePluginServesExactlyTheseRoutes` holds this table.

## Calling the API from a script

On Jellyfin 12 only the full authorization header authenticates:

```text
Authorization: MediaBrowser Token="<key>", Client="script", Device="script", DeviceId="script", Version="1"
```

`X-Emby-Token: <key>` and `?api_key=<key>` both return 401.

## Deliberate exceptions

| Exception | Reason |
| --- | --- |
| `UserViewController` declares no JSON profile | It serves an HTML fragment. `[Produces("text/html")]` is its correct declaration. |

No other exception exists. An endpoint that cannot follow a rule is added to this table with its
reason, in the same change that adds the endpoint.
