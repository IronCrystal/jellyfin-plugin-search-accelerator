using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SearchAccelerator.Configuration;
using Jellyfin.Plugin.SearchAccelerator.Search;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SearchAccelerator.Tests;

public class AcceleratedSearchManagerTests
{
    private static readonly SearchResult ExternalHit = new(Guid.NewGuid(), 0.9f);
    private static readonly SearchResult SqlHit = new(Guid.NewGuid(), 1f);
    private static readonly Movie ProbeMovie = new() { Id = Guid.NewGuid(), Name = "Known Movie" };

    private readonly FakeExternalProvider _external = new();
    private readonly FakeInternalProvider _internal = new();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly FakeJellyfinSearchManager _externalOnly = new();
    private readonly FakeJellyfinSearchManager _internalOnly = new();
    private readonly FakeJellyfinSearchManager _allProviders = new();
    private readonly FakeTimeProvider _time = new();
    private readonly PluginConfiguration _configuration = new();

    public AcceleratedSearchManagerTests()
    {
        _internal.Results.Add(SqlHit);
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { ProbeMovie });
        _libraryManager.GetItemById(ProbeMovie.Id).Returns(ProbeMovie);

        // Like Meilisearch: the probe for a known item's name finds it.
        _external.Answer = query => query.SearchTerm == ProbeMovie.Name ? new[] { new SearchResult(ProbeMovie.Id, 1f) } : _external.Results;
    }

    private AcceleratedSearchManager CreateManager()
    {
        var manager = new AcceleratedSearchManager(
            _externalOnly,
            _internalOnly,
            _allProviders,
            _libraryManager,
            NullLogger.Instance,
            () => _configuration,
            _time);
        manager.AddParts(new ISearchProvider[] { _internal, _external });
        return manager;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SearchProviderQuery Query(string term = "test") => new() { SearchTerm = term };

    private static SearchQuery HintsQuery(string term = "test") => new() { SearchTerm = term };

    [Fact]
    public async Task ExternalResults_AreReturnedWithoutRunningTheSqlSearch()
    {
        _external.Results.Add(ExternalHit);

        var results = await CreateManager().GetSearchResultsAsync(Query(), Token);

        Assert.Equal(new[] { ExternalHit }, results);
        Assert.Equal(0, _internal.Calls);
    }

    [Fact]
    public async Task ExternalHints_AreReturnedWithoutRunningTheSqlSearch()
    {
        _external.Results.Add(ExternalHit);

        var hints = await CreateManager().GetSearchHintsAsync(HintsQuery(), Token);

        Assert.Equal(ExternalHit.ItemId, Assert.Single(hints.Items).Item.Id);
        Assert.Equal(0, _internal.Calls);
    }

    [Fact]
    public async Task TrustedEmptyResult_ReturnsOnlyTheSentinel()
    {
        var results = await CreateManager().GetSearchResultsAsync(Query(), Token);

        Assert.Equal(AcceleratedSearchManager.NoResultsSentinelId, Assert.Single(results).ItemId);
        Assert.Equal(0, _internal.Calls);
    }

    [Fact]
    public async Task TrustedEmptyHints_AreEmpty()
    {
        var hints = await CreateManager().GetSearchHintsAsync(HintsQuery(), Token);

        Assert.Empty(hints.Items);
        Assert.Equal(0, hints.TotalRecordCount);
        Assert.Equal(0, _internal.Calls);
    }

    [Fact]
    public async Task EmptyResult_WithTrustOff_FallsBackToSql()
    {
        _configuration.TrustEmptyExternalResults = false;
        var manager = CreateManager();

        Assert.Equal(new[] { SqlHit }, await manager.GetSearchResultsAsync(Query(), Token));
        Assert.Equal(SqlHit.ItemId, Assert.Single((await manager.GetSearchHintsAsync(HintsQuery(), Token)).Items).Item.Id);
        Assert.Equal(2, _internal.Calls);
    }

    [Fact]
    public async Task ThrowingExternalProvider_FallsBackToSql()
    {
        _external.Throws = new InvalidOperationException("connection refused");
        var manager = CreateManager();

        Assert.Equal(new[] { SqlHit }, await manager.GetSearchResultsAsync(Query(), Token));
        Assert.Equal(SqlHit.ItemId, Assert.Single((await manager.GetSearchHintsAsync(HintsQuery(), Token)).Items).Item.Id);
    }

    [Fact]
    public async Task SlowExternalProvider_FallsBackToSql()
    {
        _configuration.ExternalTimeoutMilliseconds = 50;
        _external.Results.Add(ExternalHit);
        _external.Delay = TimeSpan.FromSeconds(10);
        var manager = CreateManager();

        Assert.Equal(new[] { SqlHit }, await manager.GetSearchResultsAsync(Query(), Token));
        Assert.Equal(SqlHit.ItemId, Assert.Single((await manager.GetSearchHintsAsync(HintsQuery(), Token)).Items).Item.Id);
    }

    [Fact]
    public async Task NoExternalProviderCanSearch_UsesSql()
    {
        _external.CanSearchQueries = false;
        var manager = CreateManager();

        Assert.Equal(new[] { SqlHit }, await manager.GetSearchResultsAsync(Query(), Token));
        Assert.Equal(SqlHit.ItemId, Assert.Single((await manager.GetSearchHintsAsync(HintsQuery(), Token)).Items).Item.Id);
    }

    [Fact]
    public async Task ProviderThatSwallowsItsOwnErrors_IsDetectedAndSqlIsUsed()
    {
        _external.Down = true;
        var manager = CreateManager();

        Assert.Equal(new[] { SqlHit }, await manager.GetSearchResultsAsync(Query(), Token));
        Assert.Equal(SqlHit.ItemId, Assert.Single((await manager.GetSearchHintsAsync(HintsQuery(), Token)).Items).Item.Id);
    }

    [Fact]
    public async Task ProviderComingBackUp_IsTrustedAgainAfterTheRetryInterval()
    {
        _external.Down = true;
        var manager = CreateManager();
        Assert.Equal(new[] { SqlHit }, await manager.GetSearchResultsAsync(Query(), Token));

        _external.Down = false;
        Assert.Equal(new[] { SqlHit }, await manager.GetSearchResultsAsync(Query(), Token));

        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(AcceleratedSearchManager.NoResultsSentinelId, Assert.Single(await manager.GetSearchResultsAsync(Query(), Token)).ItemId);
    }

    [Fact]
    public async Task HealthProbe_RunsAtMostOncePerInterval()
    {
        var manager = CreateManager();

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => manager.GetSearchResultsAsync(Query(), Token)));
        await manager.GetSearchResultsAsync(Query(), Token);

        Assert.Single(_external.Queries, query => query.SearchTerm == ProbeMovie.Name);
        Assert.Equal(0, _internal.Calls);
    }

    [Fact]
    public async Task RecentResults_MeanNoProbeIsNeeded()
    {
        var hit = new SearchResult(ProbeMovie.Id, 1f);
        _external.Answer = query => query.SearchTerm == "known" ? new[] { hit } : Array.Empty<SearchResult>();
        var manager = CreateManager();

        await manager.GetSearchResultsAsync(Query("known"), Token);
        var results = await manager.GetSearchResultsAsync(Query("nothing"), Token);

        Assert.Equal(AcceleratedSearchManager.NoResultsSentinelId, Assert.Single(results).ItemId);
        Assert.Equal(2, _external.Queries.Count);
    }

    [Fact]
    public async Task ResultsTheUserCantSee_AreTrustedWithoutAProbe()
    {
        // Like Jellyfin's SearchManager when its access filter removes every result the provider found.
        var filtering = Substitute.For<ISearchManager>();
        filtering.GetSearchResultsAsync(Arg.Any<SearchProviderQuery>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            ExternalSearchScope.Current!.MarkSearched();
            ExternalSearchScope.Current.AddResults(3);
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        });
        var manager = new AcceleratedSearchManager(filtering, _internalOnly, _allProviders, _libraryManager, NullLogger.Instance, () => _configuration, _time);
        manager.AddParts(new ISearchProvider[] { _internal, _external });

        var results = await manager.GetSearchResultsAsync(Query(), Token);

        Assert.Equal(AcceleratedSearchManager.NoResultsSentinelId, Assert.Single(results).ItemId);
        Assert.Equal(0, _internal.Calls);
        Assert.Empty(_external.Queries);
    }

    [Fact]
    public async Task EmptyLibrary_TrustsEmptyResults()
    {
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var results = await CreateManager().GetSearchResultsAsync(Query(), Token);

        Assert.Equal(AcceleratedSearchManager.NoResultsSentinelId, Assert.Single(results).ItemId);
    }

    [Fact]
    public async Task Disabled_SearchesLikeJellyfin()
    {
        _configuration.Enabled = false;
        _external.Results.Add(ExternalHit);

        var results = await CreateManager().GetSearchResultsAsync(Query(), Token);

        Assert.Equal(new[] { SqlHit, ExternalHit }, results.OrderByDescending(result => result.Score));
        Assert.Equal(1, _internal.Calls);
    }

    [Fact]
    public async Task CancelledRequest_IsNotAnsweredBySql()
    {
        _external.Delay = TimeSpan.FromSeconds(10);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateManager().GetSearchResultsAsync(Query(), cancellation.Token));
        Assert.Equal(0, _internal.Calls);
    }

    [Fact]
    public void AddParts_GivesEachInnerManagerItsOwnProviders()
    {
        var second = new FakeExternalProvider { Name = "Second", Priority = 5 };
        var manager = CreateManager();
        manager.AddParts(new ISearchProvider[] { _internal, _external, second });

        Assert.Equal(new ISearchProvider[] { second, _external, _internal }, manager.GetProviders());
        Assert.Equal(
            new ISearchProvider[] { second, _external },
            _externalOnly.Providers.Select(provider => ((TrackingExternalSearchProvider)provider).Inner));
        Assert.Equal(new ISearchProvider[] { _internal }, _internalOnly.Providers);
        Assert.Equal(new ISearchProvider[] { second, _external, _internal }, _allProviders.Providers);
    }
}
