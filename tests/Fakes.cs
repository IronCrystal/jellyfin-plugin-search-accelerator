using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;

namespace Jellyfin.Plugin.SearchAccelerator.Tests;

/// <summary>
/// Behaves like Jellyfin 12.3's SearchManager where it matters here: runs every provider that can
/// search, logs and swallows provider exceptions (cancellation included), and builds hints from results.
/// </summary>
public sealed class FakeJellyfinSearchManager : ISearchManager
{
    public IReadOnlyList<ISearchProvider> Providers { get; private set; } = Array.Empty<ISearchProvider>();

    public void AddParts(IEnumerable<ISearchProvider> providers) => Providers = providers.ToArray();

    public IReadOnlyList<ISearchProvider> GetProviders() => Providers;

    public async Task<IReadOnlyList<SearchResult>> GetSearchResultsAsync(SearchProviderQuery query, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        foreach (var provider in Providers.Where(provider => provider.CanSearch(query)))
        {
            try
            {
                if (provider is IExternalSearchProvider external)
                {
                    await foreach (var result in external.SearchAsync(query, cancellationToken))
                    {
                        results.Add(result);
                    }
                }
                else
                {
                    results.AddRange(await provider.SearchAsync(query, cancellationToken));
                }
            }
            catch (Exception)
            {
            }
        }

        return results;
    }

    public async Task<QueryResult<SearchHintInfo>> GetSearchHintsAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        var results = await GetSearchResultsAsync(new SearchProviderQuery { SearchTerm = query.SearchTerm }, cancellationToken);
        if (results.Count == 0)
        {
            return new QueryResult<SearchHintInfo>();
        }

        var hints = results.Select(result => new SearchHintInfo { Item = new Movie { Id = result.ItemId, Name = "hit" } }).ToList();
        return new QueryResult<SearchHintInfo>(0, hints.Count, hints);
    }
}

public sealed class FakeExternalProvider : IExternalSearchProvider
{
    public List<SearchResult> Results { get; } = new();

    public bool CanSearchQueries { get; set; } = true;

    public Exception? Throws { get; set; }

    public TimeSpan Delay { get; set; }

    // Like Shadowghost's Meilisearch provider while Meilisearch is down: catches its error, yields nothing.
    public bool Down { get; set; }

    public Func<SearchProviderQuery, IEnumerable<SearchResult>>? Answer { get; set; }

    public List<SearchProviderQuery> Queries { get; } = new();

    public string Name { get; set; } = "Fake external";

    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    public int Priority { get; set; } = 10;

    public bool CanSearch(SearchProviderQuery query) => CanSearchQueries;

    public async IAsyncEnumerable<SearchResult> SearchAsync(SearchProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (Queries)
        {
            Queries.Add(query);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (Throws is not null)
        {
            throw Throws;
        }

        if (Down)
        {
            yield break;
        }

        foreach (var result in Answer?.Invoke(query) ?? Results)
        {
            yield return result;
        }
    }

    Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

public sealed class FakeInternalProvider : IInternalSearchProvider
{
    public List<SearchResult> Results { get; } = new();

    public int Calls { get; private set; }

    public string Name => "Fake SQL";

    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    public int Priority => 1000;

    public bool CanSearch(SearchProviderQuery query) => true;

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<SearchResult>>(Results.ToArray());
    }
}
