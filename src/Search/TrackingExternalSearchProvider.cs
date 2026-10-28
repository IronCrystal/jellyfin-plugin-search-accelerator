using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.SearchAccelerator.Search;

/// <summary>
/// Passes everything through to an external provider and records what it did in
/// <see cref="ExternalSearchScope.Current"/>. Internal so Jellyfin's export scan never instantiates it
/// as a provider of its own.
/// </summary>
internal sealed class TrackingExternalSearchProvider : IExternalSearchProvider
{
    public TrackingExternalSearchProvider(IExternalSearchProvider inner)
    {
        Inner = inner;
    }

    public IExternalSearchProvider Inner { get; }

    public string Name => Inner.Name;

    public MetadataPluginType Type => Inner.Type;

    public int Priority => Inner.Priority;

    public bool CanSearch(SearchProviderQuery query)
    {
        bool canSearch;
        try
        {
            canSearch = Inner.CanSearch(query);
        }
        catch
        {
            ExternalSearchScope.Current?.MarkFailed();
            throw;
        }

        if (canSearch)
        {
            ExternalSearchScope.Current?.MarkSearched();
        }

        return canSearch;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scope = ExternalSearchScope.Current;
        var enumerator = Inner.SearchAsync(query, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch
                {
                    scope?.MarkFailed();
                    throw;
                }

                if (!hasNext)
                {
                    break;
                }

                scope?.AddResults(1);
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(
        SearchProviderQuery query,
        CancellationToken cancellationToken)
    {
        var scope = ExternalSearchScope.Current;
        try
        {
            var results = await ((ISearchProvider)Inner).SearchAsync(query, cancellationToken).ConfigureAwait(false);
            scope?.AddResults(results.Count);
            return results;
        }
        catch
        {
            scope?.MarkFailed();
            throw;
        }
    }
}
