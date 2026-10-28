# Search Accelerator for Jellyfin

Makes Jellyfin search as fast as the external search provider behind it, such as
[Shadowghost's Meilisearch plugin](https://github.com/Shadowghost/jellyfin-plugin-meilisearch).

With an external search provider installed, Jellyfin 12.3 still runs its own database search alongside it and waits for
both, so search is never faster than the built-in search. This plugin asks the external provider first and only runs
Jellyfin's search when the provider can't answer. It also routes **People** search, which Jellyfin never sends to search
providers, through the provider.

It works with any external search provider and doesn't index anything itself. Indexing stays with the provider plugin.

## Compatibility

Jellyfin **12.3** (built against 12.3.0), with an external search provider plugin installed. Without one, Jellyfin searches as normal.

## Install

### From a plugin repository (recommended)

1. Dashboard → Plugins → Repositories → **+**
2. Name: `Search Accelerator`, URL:
   `https://raw.githubusercontent.com/IronCrystal/jellyfin-plugin-search-accelerator/main/manifest.json`
3. Install **Search Accelerator** from the catalog, then restart Jellyfin.

### Manually

Copy `Jellyfin.Plugin.SearchAccelerator.dll` (from a release zip, or `./build.sh`) into
`<jellyfin data>/plugins/SearchAccelerator_1.0.0.0/` and restart Jellyfin.

## What it changes

| Request | Jellyfin 12.3 | With this plugin |
|---|---|---|
| Item search (`/Items?searchTerm=`), search hints (`/Search/Hints`) | Runs the external provider **and** the database search, and waits for both | External provider only. Its results are still filtered by Jellyfin for what the user can see |
| A search the provider finds nothing for (such as the web app's "Videos" row) | Database search, then a second database name search | Returns nothing straight away |
| People (`/Persons?searchTerm=`) | Database "name contains" scan; providers never asked | External provider |

### Fallback to Jellyfin's search

Jellyfin's own search answers when:

- no external provider can search the query,
- the provider throws, or takes longer than the timeout (default 1500 ms),
- the provider returns nothing and either **Trust "no results"** is off, or the provider seems to be down.

Shadowghost's Meilisearch provider doesn't report errors: when Meilisearch is unreachable it just returns no results, which
looks like "nothing matched". So an empty answer is only trusted if the provider found something in the last 10 seconds.
If it hasn't, the plugin searches the provider for the name of an item known to be in the library (one quick query, at most
once every 10 seconds). If that finds nothing too, the provider is treated as down and Jellyfin's search is used, rechecked
every 5 seconds.

### People search

Answered by the provider unless the request also filters by anything the provider can't apply (`nameStartsWith`,
`nameLessThan`, `nameStartsWithOrGreater`, `personTypes`, `excludePersonTypes`, `appearsInItemId`, `isFavorite` or the
`IsFavorite` filter, `parentId`), or the user has library, parental-rating or tag restrictions. People aren't owned by a
library; which of them a restricted user may see depends on the items they're credited on, and only Jellyfin's own query
checks that. Those requests go to Jellyfin unchanged.

## Settings

| Setting | Default | Effect |
|---|---|---|
| Use the external search provider first | on | Master switch. When off, Jellyfin searches as normal. |
| Trust "no results" from the external provider | on | An empty answer from a healthy provider is final. Turn off if the index is often out of date. |
| External provider timeout (ms) | 1500 | Slower answers fall back to Jellyfin's search. |
| Search people with the external provider | on | See above. |
| Log every search | off | Logs which search answered each query, with timings. |

## Security

- People requests are handled inside Jellyfin's own request pipeline, after Jellyfin's login check: no valid login means
  **401** from Jellyfin, and asking for another user's results without being an administrator means **403**, the same rule
  as Jellyfin's.
- Item search results from the provider go through Jellyfin's own user-access filtering (Jellyfin's `SearchManager` does
  the searching, and `ItemsController` loads the items for the user), so nothing from a library the user can't see is returned.

## How it works

Jellyfin registers its `SearchManager` before plugins register their services, so the plugin replaces that registration
with a wrapper. The wrapper creates three instances of Jellyfin's own `SearchManager`: one given only the external
providers, one only the internal (database) provider, and one with all of them for when the plugin is switched off.

When the provider finds nothing, the wrapper returns a single placeholder result with an id no item has. If it returned an
empty list, Jellyfin's `ItemsController` would run its old database name search as well. With the placeholder, it looks up
that id, finds nothing, and responds with zero items.

This depends on Jellyfin internals (`ISearchManager`, `ItemsController`'s handling of an empty search result, and the
`Persons`/`GetPersons` controller and action names). Retest after each Jellyfin upgrade.

## Tested

Unit tests cover the search manager (external first, trusted empty results, fallback on error, timeout, provider down,
or no provider able to search) and the People filter (each pass-through case, 403 for another user's id, fallback on error).

End to end on a local Jellyfin 12.3 with Meilisearch 1.34 and Shadowghost's Meilisearch plugin 1.0.0.0, using a synthetic
library (3,000 movies, 100 series, 5,000 episodes, 4,800 people) and three users (admin, an unrestricted user, and a user
limited to one library):

- 103 of 105 searches return exactly the same items as without the plugin. The other two are People searches with over 100
  matches, where the plugin returns the 100 most relevant instead of the first 100 alphabetically.
- Searches that find nothing are about 4× faster (35 ms → 9 ms); the others are the same or faster. This library is far
  smaller than a real one, so the absolute gains are much smaller than on a large server.
- 401 without a login, 403 for another user's results, and nothing from libraries the user can't open.
- With Meilisearch stopped, searches fall back to Jellyfin's search and still find results; when it comes back, the plugin
  switches back within seconds.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
./build.sh
```

Runs the tests and writes `dist/Jellyfin.Plugin.SearchAccelerator.dll`.

## Publishing a release

Tag and push: `git tag v1.0.0 && git push origin v1.0.0`.

The release workflow runs the tests, builds the plugin, attaches the zip to a GitHub release, and adds the version
(with its checksum) to `manifest.json` on `main`, which is what Jellyfin's plugin catalog reads.
