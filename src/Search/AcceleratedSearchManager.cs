using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SearchAccelerator.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SearchAccelerator.Search;

/// <summary>
/// Replaces Jellyfin's ISearchManager. Jellyfin's own SearchManager runs the external providers and its
/// SQL search in parallel and always waits for both. This asks the external providers first and only
/// runs the SQL search when they can't answer.
/// </summary>
/// <remarks>
/// The searching itself is done by instances of Jellyfin's own SearchManager, each given a subset of the
/// providers, so its user-access filtering and search-hint logic are kept unchanged.
/// </remarks>
public sealed class AcceleratedSearchManager : ISearchManager
{
    /// <summary>
    /// Returned instead of an empty list when the external providers answered "no matches".
    /// ItemsController (Jellyfin 12.3) treats an empty result as "the providers had nothing" and runs
    /// its legacy SQL name search (InternalItemsQuery.SearchTerm) instead, which is the slowest part of
    /// a search that finds nothing. Any non-empty result skips that; it then loads the returned ids with
    /// folder.GetItems, which finds nothing for an id that doesn't exist and so returns zero items.
    /// </summary>
    public static readonly Guid NoResultsSentinelId = new("00000000-5ea4-4c4a-8cce-1e7a00000001");

    private static readonly TimeSpan _healthyFor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _unhealthyFor = TimeSpan.FromSeconds(5);

    private readonly ISearchManager _externalOnly;
    private readonly ISearchManager _internalOnly;
    private readonly ISearchManager _allProviders;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;
    private readonly Func<PluginConfiguration?> _getConfiguration;
    private readonly ExternalProviderHealth _health;
    private IReadOnlyList<ISearchProvider> _providers = Array.Empty<ISearchProvider>();
    private Guid _probeItemId;

    /// <param name="externalOnly">Jellyfin's SearchManager, given only the external providers.</param>
    /// <param name="internalOnly">Jellyfin's SearchManager, given only the internal (SQL) providers.</param>
    /// <param name="allProviders">Jellyfin's SearchManager with every provider, used when the plugin is off.</param>
    public AcceleratedSearchManager(
        ISearchManager externalOnly,
        ISearchManager internalOnly,
        ISearchManager allProviders,
        ILibraryManager libraryManager,
        ILogger<AcceleratedSearchManager> logger)
        : this(externalOnly, internalOnly, allProviders, libraryManager, logger, () => Plugin.Instance?.Configuration, TimeProvider.System)
    {
    }

    internal AcceleratedSearchManager(
        ISearchManager externalOnly,
        ISearchManager internalOnly,
        ISearchManager allProviders,
        ILibraryManager libraryManager,
        ILogger logger,
        Func<PluginConfiguration?> getConfiguration,
        TimeProvider timeProvider)
    {
        _externalOnly = externalOnly;
        _internalOnly = internalOnly;
        _allProviders = allProviders;
        _libraryManager = libraryManager;
        _logger = logger;
        _getConfiguration = getConfiguration;
        _health = new ExternalProviderHealth(ProbeAsync, timeProvider, _healthyFor, _unhealthyFor);
    }

    public void AddParts(IEnumerable<ISearchProvider> providers)
    {
        var all = providers.OrderBy(provider => provider.Priority).ToArray();
        _providers = all;

        _externalOnly.AddParts(all
            .OfType<IExternalSearchProvider>()
            .Select(provider => new TrackingExternalSearchProvider(provider))
            .ToArray());
        _internalOnly.AddParts(all.OfType<IInternalSearchProvider>().ToArray());
        _allProviders.AddParts(all);
    }

    public IReadOnlyList<ISearchProvider> GetProviders() => _providers;

    public async Task<IReadOnlyList<SearchResult>> GetSearchResultsAsync(
        SearchProviderQuery query,
        CancellationToken cancellationToken = default)
    {
        var configuration = _getConfiguration();
        if (configuration is null || !configuration.Enabled)
        {
            return await _allProviders.GetSearchResultsAsync(query, cancellationToken).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        var external = await TryGetExternalResultsAsync(query, configuration, cancellationToken).ConfigureAwait(false);
        if (external is null)
        {
            var results = await _internalOnly.GetSearchResultsAsync(query, cancellationToken).ConfigureAwait(false);
            LogPath(configuration, "results", query.SearchTerm, "internal (fallback)", results.Count, stopwatch);
            return results;
        }

        LogPath(configuration, "results", query.SearchTerm, "external", external.Count, stopwatch);
        return external.Count > 0 ? external : new[] { new SearchResult(NoResultsSentinelId, 0f) };
    }

    public async Task<QueryResult<SearchHintInfo>> GetSearchHintsAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var configuration = _getConfiguration();
        if (configuration is null || !configuration.Enabled)
        {
            return await _allProviders.GetSearchHintsAsync(query, cancellationToken).ConfigureAwait(false);
        }

        var stopwatch = Stopwatch.StartNew();
        var external = await SearchExternalAsync(
            (manager, token) => manager.GetSearchHintsAsync(query, token),
            query.SearchTerm,
            configuration,
            cancellationToken).ConfigureAwait(false);

        // Jellyfin's hints path returns early on zero candidates, so an empty answer needs no sentinel.
        if (external is { } answer
            && (answer.Value.Items.Count > 0 || answer.Value.TotalRecordCount > 0
                || await IsTrustedEmptyAsync(answer.Scope, configuration).ConfigureAwait(false)))
        {
            LogPath(configuration, "hints", query.SearchTerm, "external", answer.Value.TotalRecordCount, stopwatch);
            return answer.Value;
        }

        var hints = await _internalOnly.GetSearchHintsAsync(query, cancellationToken).ConfigureAwait(false);
        LogPath(configuration, "hints", query.SearchTerm, "internal (fallback)", hints.TotalRecordCount, stopwatch);
        return hints;
    }

    /// <summary>
    /// Searches the external providers only, with Jellyfin's user-access filtering applied.
    /// </summary>
    /// <returns>
    /// The results; an empty list when the providers found nothing and that answer is trusted; or null when
    /// the external providers can't answer (none can search the query, one failed or timed out, or the
    /// empty answer isn't trusted) and the caller should use Jellyfin's own search.
    /// </returns>
    internal async Task<IReadOnlyList<SearchResult>?> TryGetExternalResultsAsync(
        SearchProviderQuery query,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var external = await SearchExternalAsync(
            (manager, token) => manager.GetSearchResultsAsync(query, token),
            query.SearchTerm,
            configuration,
            cancellationToken).ConfigureAwait(false);
        if (external is not { } answer)
        {
            return null;
        }

        if (answer.Value.Count > 0)
        {
            _probeItemId = answer.Value[0].ItemId;
            return answer.Value;
        }

        return await IsTrustedEmptyAsync(answer.Scope, configuration).ConfigureAwait(false)
            ? Array.Empty<SearchResult>()
            : null;
    }

    private async Task<(T Value, ExternalSearchScope Scope)?> SearchExternalAsync<T>(
        Func<ISearchManager, CancellationToken, Task<T>> search,
        string searchTerm,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var scope = new ExternalSearchScope();
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, configuration.ExternalTimeoutMilliseconds));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        T value;
        ExternalSearchScope.Current = scope;
        try
        {
            // WaitAsync also covers a provider that ignores the cancellation token.
            value = await search(_externalOnly, timeoutSource.Token).WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("External search for '{SearchTerm}' took longer than {Timeout} ms; using Jellyfin's search", searchTerm, timeout.TotalMilliseconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "External search for '{SearchTerm}' failed; using Jellyfin's search", searchTerm);
            return null;
        }
        finally
        {
            ExternalSearchScope.Current = null;
        }

        // Jellyfin's SearchManager catches provider errors, including the cancellation, and returns no results.
        if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("External search for '{SearchTerm}' took longer than {Timeout} ms; using Jellyfin's search", searchTerm, timeout.TotalMilliseconds);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (scope.Failed)
        {
            _logger.LogWarning("An external search provider failed for '{SearchTerm}'; using Jellyfin's search", searchTerm);
            return null;
        }

        if (!scope.Searched)
        {
            return null;
        }

        if (scope.ResultCount > 0)
        {
            _health.ReportHealthy();
        }

        return (value, scope);
    }

    private async Task<bool> IsTrustedEmptyAsync(ExternalSearchScope scope, PluginConfiguration configuration)
    {
        if (!configuration.TrustEmptyExternalResults)
        {
            return false;
        }

        // The provider did find items; the user just isn't allowed to see them.
        if (scope.ResultCount > 0)
        {
            return true;
        }

        var healthy = await _health.IsHealthyAsync().ConfigureAwait(false);
        if (!healthy)
        {
            _logger.LogWarning("The external search provider isn't finding known items; using Jellyfin's search until it does");
        }

        return healthy;
    }

    /// <summary>
    /// Searches the external providers for the name of an item known to exist.
    /// </summary>
    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var name = FindProbeName();
        if (name is null)
        {
            // An empty library: there's nothing for Jellyfin's search to find either.
            return true;
        }

        var configuration = _getConfiguration() ?? new PluginConfiguration();
        var probe = await SearchExternalAsync(
            (manager, token) => manager.GetSearchResultsAsync(new SearchProviderQuery { SearchTerm = name, Limit = 1 }, token),
            name,
            configuration,
            cancellationToken).ConfigureAwait(false);
        return probe is { } answer && answer.Value.Count > 0;
    }

    private string? FindProbeName()
    {
        if (!_probeItemId.Equals(Guid.Empty) && _libraryManager.GetItemById(_probeItemId) is { Name: { Length: > 0 } name })
        {
            return name;
        }

        var item = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum },
            Recursive = true,
            IsVirtualItem = false,
            Limit = 1,
        }).FirstOrDefault(found => !string.IsNullOrWhiteSpace(found.Name));
        if (item is null)
        {
            return null;
        }

        _probeItemId = item.Id;
        return item.Name;
    }

    private void LogPath(PluginConfiguration configuration, string kind, string searchTerm, string path, int count, Stopwatch stopwatch)
    {
        if (configuration.DebugLogging)
        {
            _logger.LogInformation(
                "Search {Kind} for '{SearchTerm}' served by {Path}: {Count} results in {Elapsed} ms",
                kind,
                searchTerm,
                path,
                count,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
