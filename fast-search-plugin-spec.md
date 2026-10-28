# Jellyfin "Fast Search" companion plugin: design brief

## Goal

Make Jellyfin 12.3 search as fast as the Meilisearch index behind it. Today, Meilisearch answers in 4–22 ms but the same searches take 3.5–7 s in Jellyfin, because Jellyfin keeps running its own slow database search alongside it.

**Approach:** write a small **companion plugin**. Do **not** fork Shadowghost's Meilisearch plugin (reasons below). The companion works with any installed external search provider. In practice that's Shadowghost's Meilisearch plugin.

## Environment

- Jellyfin **12.3** (Docker on Unraid), ~5,200 movies, ~1,600 series, ~56,000 episodes; ~168k documents in the Meilisearch index.
- Search plugin: **Shadowghost/jellyfin-plugin-meilisearch** (stable feed 1.0.0.0), index `jellyfin-shadow`, Meilisearch 1.34.
- The owner already maintains a similar plugin, **IronCrystal/jellyfin-continue-watching** ("Continue Watching Plus"), built with .NET 10 and Jellyfin.Controller 12.3.0. It uses an MVC action filter, has unit tests, and has a GitHub release workflow that publishes `manifest.json`. **Reuse its structure, build setup, and release workflow.**

## Evidence (measured)

Web client search for "test", from the browser network tab:

| Request | Time | Notes |
|---|---|---|
| `Items?…mediaTypes=Video…` ("Videos" row) | 6.91 s | 0 results |
| `Persons?searchTerm=test` | 4.21 s | |
| `Items?…Movie,Series…` (main results) | 3.58 s | 1.5 MB |
| Artists / Studios / Live TV programs | < 0.35 s | |

A direct Meilisearch query for the same kind of term returns in **4–22 ms**.

## Root causes (verified in Jellyfin source, current master)

1. **`SearchManager.GetSearchResultsAsync`** (`Emby.Server.Implementations/Library/Search/SearchManager.cs`) starts the external providers *and* the internal `SqlSearchProvider` in parallel, then `await Task.WhenAll(externalTask, internalTask)`. External results are used if there are any, but the request **always waits for the SQL search**. So search is never faster than the built-in search.
2. **`SqlSearchProvider.CanSearch` always returns `true`**, and there's no server setting to disable or reorder providers.
3. **`ItemsController`** (`Jellyfin.Api/Controllers/ItemsController.cs`, around lines 355–450): if `GetSearchResultsAsync` returns **0 results**, it sets `SearchTerm = searchTerm` on the `InternalItemsQuery`, which runs Jellyfin's legacy SQL name search **again** inside `GetItems`. This is why the 0-result "Videos" query is the slowest.
4. **`PersonsController.GetPersons`** never uses search providers. It calls `_libraryManager.GetPeopleItems(new InternalPeopleQuery { NameContains = searchTerm, … })`, a SQL contains-scan. The Meilisearch index already contains people (`itemType: "Person"` documents), but Jellyfin never asks it.
5. Jellyfin **12.2** (PR #18223) made `SqlSearchProvider` also scan `SortName` with `LIKE '%…%'`, so the built-in search does more work per query than it used to.

## Why a companion plugin and not a fork

- The problem is in **Jellyfin's search manager and People endpoint**, not in the Meilisearch plugin. A fork would mean maintaining Shadowghost's whole indexer and sync engine to change behavior that lives outside it.
- Shadowghost's plugin is actively developed (stable and unstable feeds). A fork falls behind.
- A provider-agnostic companion keeps working if the search plugin changes or is swapped.
- Plugin name must be **unique** (not "Meilisearch"). Jellyfin groups plugin folders by **name** on startup and deletes those it considers older versions.

## Design

### Part 1: replace `ISearchManager` with a decorator

**Hook (verified):** in `ApplicationHost`, core services are registered first (`RegisterServices`), then plugin `IPluginServiceRegistrator`s run (`_pluginManager.RegisterServices`). Core registers `serviceCollection.AddSingleton<ISearchManager, SearchManager>()`. Later, `Resolve<ISearchManager>().AddParts(GetExports<ISearchProvider>())` feeds it the providers. A plugin registration that replaces `ISearchManager` therefore wins.

**Avoid referencing `Emby.Server.Implementations`** (it's not in the plugin NuGet packages). Instead:
1. In `RegisterServices`, find the existing `ServiceDescriptor` for `ISearchManager` and capture its `ImplementationType` (Jellyfin's `SearchManager`).
2. Replace the registration with a factory that builds the decorator. The decorator creates **two** instances of the original type with `ActivatorUtilities.CreateInstance(serviceProvider, implementationType)`:
   - `externalOnly`, which receives only `IExternalSearchProvider`s,
   - `internalOnly`, which receives only `IInternalSearchProvider`s.
   Reusing Jellyfin's own implementation keeps its **user-access filtering** (`FilterByUserAccessAsync`) and its `GetSearchHintsAsync` logic intact.

**Behavior:**
- `AddParts(providers)`: split by interface, pass each set to its inner manager, and keep the full list for `GetProviders()`.
- `GetSearchResultsAsync(query)`:
  1. If no external provider `CanSearch(query)`, return `internalOnly.GetSearchResultsAsync(query)`, which is today's behavior.
  2. Otherwise await `externalOnly.GetSearchResultsAsync(query)`, with a timeout (config, default ~1500 ms).
  3. If there are results, return them. The SQL search never runs.
  4. If it's empty and **"Trust empty external results"** is on (the default), return a **single sentinel `SearchResult`** with a fixed, non-existent GUID. A non-empty result stops `ItemsController` from falling back to its SQL `SearchTerm` query (root cause 3). `GetItems` with an unknown ID should return nothing quickly. **Verify this**, including `TotalRecordCount` and that the sentinel never appears in a response.
  5. On timeout or exception, or if trust is off and the result was empty, fall back to `internalOnly`.
- `GetSearchHintsAsync(query)` (used by `/Search/Hints`): same pattern. External first. If it's empty and trusted, return an empty `QueryResult` (no sentinel needed, since that path returns early on 0 candidates). On error or timeout, use `internalOnly`.

**Open question to check:** does Shadowghost's provider `CanSearch` return `false` when its health monitor sees Meilisearch as unreachable? If yes, fallback is automatic. If not, the timeout and exception handling covers it.

### Part 2: People search through the providers

Add an `IAsyncActionFilter`, registered via `MvcOptions.Filters.AddService` the same way Continue Watching Plus does it, on controller `Persons`, action `GetPersons`.

**Handle only when** `searchTerm` is non-empty **and** none of these are set: `nameStartsWith`, `nameLessThan`, `nameStartsWithOrGreater`, `personTypes`, `excludePersonTypes`, `appearsInItemId`, `isFavorite`, or the `IsFavorite` filter. **Also pass through** if the user has content restrictions (`user.HasContentRestrictions()`), because Jellyfin's people access check (`BuildAccessFilter`) depends on the items each person is credited on. Everything else goes to Jellyfin unchanged (`await next()`).

**Handling:**
1. Resolve the user with the same rule as Jellyfin's `RequestHelpers.GetUserId`: no ID means the caller's own; another user's ID requires the Administrator role; otherwise throw `MediaBrowser.Controller.Net.SecurityException`, which Jellyfin returns as 403. Continue Watching Plus already has this as `ResolveRequestUserId`, so copy it.
2. Call `ISearchManager.GetSearchResultsAsync` with `IncludeItemTypes = [BaseItemKind.Person]`, the user ID, and a limit of (startIndex + limit) or a sane cap.
3. Load each hit with `ILibraryManager.GetItemById<Person>(id)`, skipping missing ones.
4. Build DTOs exactly as Jellyfin does: `IDtoService.GetItemByNameDto(person, dtoOptions, null, user)`, with `DtoOptions` built from `fields`, `enableImages`, `enableUserData`, `imageTypeLimit`, `enableImageTypes`. Copy the DTO options helper from Continue Watching Plus.
5. Apply `startIndex`/`limit`, set `TotalRecordCount`, and return `OkObjectResult(QueryResult<BaseItemDto>)`.
6. On any exception, log it and call `await next()` so Jellyfin answers normally.

### Configuration page

- Enabled (default on)
- Trust empty external results (default on)
- External provider timeout, ms (default 1500)
- Accelerate People search (default on)
- Debug logging: log which path served each query, with timing

### Security

- The action filter runs inside MVC after Jellyfin's `[Authorize]`, so it never handles unauthenticated requests. Also check `HttpContext.User.Identity.IsAuthenticated`.
- The search-manager decorator relies on Jellyfin's own `SearchManager` access filtering for external results, and the `ItemsController` still loads items through `folder.GetItems` with the user's access applied.
- People acceleration is skipped for users with content restrictions (see Part 2).

### Not in scope

- The web client still sends about six requests per keystroke (jellyfin-web issue #6793). That's a client issue.
- Indexing. That stays with the Meilisearch plugin.

## Tests

**Unit tests** (xUnit with NSubstitute, as in Continue Watching Plus):
- An external provider with results means the internal provider is never called.
- External empty and trusted returns the sentinel from `GetSearchResultsAsync` and an empty result from `GetSearchHintsAsync`, and the internal provider isn't called.
- External empty with trust off, an external that throws, an external that times out, or no external able to search: all fall back to internal.
- `AddParts` splits providers correctly, and `GetProviders` returns all of them.
- People filter: passes through for each excluded parameter and for restricted users; returns 403 for another user's ID as a non-admin; falls back on exception.

**On the server** (DevTools Network tab, same searches as above):
- "Videos" row: **6.9 s → well under 0.5 s**
- People: **4.2 s → under 0.5 s**
- Main results: **3.6 s → roughly the time for Jellyfin to load the matched items**
- No change in which results appear for a non-admin user, and nothing from libraries that user can't access.
- Stop the Meilisearch container: search must still work (slowly) through the fallback.

## Risks and fragile points

- **Jellyfin internals:** the `ISearchManager` interface, the `ItemsController` zero-result fallback, and the `Persons`/`GetPersons` controller and action names could change in a future release. Pin `Jellyfin.Controller` to the server version and retest after each Jellyfin upgrade.
- **The sentinel workaround** depends on `ItemsController`'s current "0 results → SQL fallback" behavior. Verify it, and document it in code comments.
- **"Trust empty results"** means a stale or empty index gives missing results instead of slow ones. Shadowghost's real-time sync and health monitor make that unlikely, and the setting can be turned off.

## Install and release

- Same as Continue Watching Plus: the GitHub release workflow publishes `manifest.json` on tag, and users add the repository URL in Dashboard → Plugins → Repositories.
- On this Unraid server, plugin folders under `/mnt/user/appdata/jellyfin/plugins` sometimes can't be deleted while Jellyfin runs ("Directory not empty", from the FUSE share layer), which leaves plugins marked `Deleted`. Mapping Jellyfin's `/config` to `/mnt/cache/appdata/jellyfin` avoids this.

## Upstream

Also open a Jellyfin issue proposing the real fix: in `SearchManager.GetSearchResultsAsync`, await external providers first and run or await the internal provider only when external returns nothing or fails. Ideally also add an option to treat a healthy external provider's empty result as final, and route `PersonsController` searches through `ISearchManager`. Shadowghost authored both the provider system and the Meilisearch plugin, so tag him. If it lands, retire this plugin.

## Reference files (jellyfin/jellyfin master)

- `Emby.Server.Implementations/Library/Search/SearchManager.cs`: `GetSearchResultsAsync`, `GetSearchHintsAsync`, `FilterByUserAccessAsync`
- `Emby.Server.Implementations/Library/Search/SqlSearchProvider.cs`
- `Emby.Server.Implementations/ApplicationHost.cs`: `RegisterServices` order, `AddSingleton<ISearchManager, SearchManager>`, `AddParts(GetExports<ISearchProvider>())`
- `Jellyfin.Api/Controllers/ItemsController.cs`: search provider call and the `SearchTerm = searchResultScores is null ? searchTerm : null` fallback
- `Jellyfin.Api/Controllers/PersonsController.cs`: `GetPersons`, `BuildAccessFilter`
- `MediaBrowser.Controller/Library/ISearchManager.cs`, `ISearchProvider.cs`, `IExternalSearchProvider.cs`, `IInternalSearchProvider.cs`, `SearchProviderQuery.cs`, `SearchResult.cs`
- `Jellyfin.Api/Helpers/RequestHelpers.cs`: `GetUserId` (user ID / admin rule)
