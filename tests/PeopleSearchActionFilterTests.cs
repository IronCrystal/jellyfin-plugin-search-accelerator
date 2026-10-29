using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SearchAccelerator.Api;
using Jellyfin.Plugin.SearchAccelerator.Configuration;
using Jellyfin.Plugin.SearchAccelerator.Search;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Jellyfin.Plugin.SearchAccelerator.Tests;

public class PeopleSearchActionFilterTests
{
    private readonly User _alice = new("alice", "auth", "reset");
    private readonly User _bob = new("bob", "auth", "reset");
    private readonly Person _tom = new() { Id = Guid.NewGuid(), Name = "Tom Hanks" };
    private readonly Person _tim = new() { Id = Guid.NewGuid(), Name = "Tim Allen" };
    private readonly FakeExternalProvider _external = new();
    private readonly FakeInternalProvider _internal = new();
    private readonly IUserManager _userManager = Substitute.For<IUserManager>();
    private readonly ILibraryManager _libraryManager = Substitute.For<ILibraryManager>();
    private readonly IDtoService _dtoService = Substitute.For<IDtoService>();
    private readonly PluginConfiguration _configuration = new();
    private readonly FakeJellyfinSearchManager _externalOnly = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ListLogger<PeopleSearchActionFilter> _log = new();
    private readonly List<VirtualFolderInfo> _libraries = new()
    {
        new VirtualFolderInfo { Name = "Movies", CollectionType = CollectionTypeOptions.movies },
        new VirtualFolderInfo { Name = "Shows", CollectionType = CollectionTypeOptions.tvshows },
    };

    private bool _nextCalled;

    public PeopleSearchActionFilterTests()
    {
        _alice.AddDefaultPermissions();
        _bob.AddDefaultPermissions();
        _userManager.GetUserById(_alice.Id).Returns(_alice);
        _userManager.GetUserById(_bob.Id).Returns(_bob);
        _libraryManager.GetItemById(_tom.Id).Returns(_tom);
        _libraryManager.GetItemById(_tim.Id).Returns(_tim);
        _dtoService.GetItemByNameDto(Arg.Any<BaseItem>(), Arg.Any<DtoOptions>(), Arg.Any<List<BaseItem>?>(), Arg.Any<User?>())
            .Returns(callInfo => new BaseItemDto { Id = callInfo.Arg<BaseItem>().Id, Name = callInfo.Arg<BaseItem>().Name });
        _libraryManager.GetVirtualFolders().Returns(_ => _libraries.ToList());
        _external.Results.Add(new SearchResult(_tom.Id, 1f));
        _external.Results.Add(new SearchResult(Guid.NewGuid(), 0.8f)); // in the index but no longer in Jellyfin
        _external.Results.Add(new SearchResult(_tim.Id, 0.5f));
    }

    private PeopleSearchActionFilter CreateFilter(ISearchManager? searchManager = null)
    {
        if (searchManager is null)
        {
            var manager = new AcceleratedSearchManager(
                _externalOnly,
                new FakeJellyfinSearchManager(),
                new FakeJellyfinSearchManager(),
                _libraryManager,
                NullLogger.Instance,
                () => _configuration,
                new FakeTimeProvider());
            manager.AddParts(new ISearchProvider[] { _internal, _external });
            searchManager = manager;
        }

        return new PeopleSearchActionFilter(
            searchManager,
            _userManager,
            _libraryManager,
            _dtoService,
            _log,
            () => _configuration,
            _time);
    }

    // Exactly what Jellyfin Web sends for the People row of a search.
    private Dictionary<string, object?> WebClientRequest(string searchTerm = "t") => new()
    {
        ["limit"] = 100,
        ["searchTerm"] = searchTerm,
        ["fields"] = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.CanDelete, ItemFields.MediaSourceCount },
        ["imageTypeLimit"] = 1,
        ["excludePersonTypes"] = new[] { "Artist", "AlbumArtist" },
        ["userId"] = _alice.Id,
    };

    private static ClaimsPrincipal Principal(User user, bool isAdministrator = false)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim("Jellyfin-UserId", user.Id.ToString("N")),
                new Claim(ClaimTypes.Role, isAdministrator ? "Administrator" : "User"),
            },
            "CustomAuthentication"));

    private async Task<ActionExecutingContext> RunAsync(
        Dictionary<string, object?> arguments,
        ClaimsPrincipal? principal = null,
        PeopleSearchActionFilter? filter = null,
        string controller = "Persons",
        string action = "GetPersons")
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext { User = principal ?? Principal(_alice) },
            new RouteData(),
            new ControllerActionDescriptor { ControllerName = controller, ActionName = action });
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());

        await (filter ?? CreateFilter()).OnActionExecutionAsync(context, () =>
        {
            _nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
        });
        return context;
    }

    private static QueryResult<BaseItemDto> Response(ActionExecutingContext context)
        => Assert.IsType<QueryResult<BaseItemDto>>(Assert.IsType<OkObjectResult>(context.Result).Value);

    [Fact]
    public async Task Search_IsAnsweredFromTheProvider()
    {
        var context = await RunAsync(new() { ["searchTerm"] = "t", ["fields"] = Array.Empty<ItemFields>() });

        Assert.False(_nextCalled);
        var response = Response(context);
        Assert.Equal(new[] { "Tom Hanks", "Tim Allen" }, response.Items.Select(dto => dto.Name));
        Assert.Equal(2, response.TotalRecordCount);
        Assert.Equal(0, _internal.Calls);
        var query = Assert.Single(_external.Queries);
        Assert.Equal(new[] { BaseItemKind.Person }, query.IncludeItemTypes);
        Assert.Equal(_alice.Id, query.UserId);
    }

    [Fact]
    public async Task Paging_IsApplied()
    {
        var response = Response(await RunAsync(new() { ["searchTerm"] = "t", ["startIndex"] = 1, ["limit"] = 1 }));

        Assert.Equal("Tim Allen", Assert.Single(response.Items).Name);
        Assert.Equal(2, response.TotalRecordCount);
        Assert.Equal(1, response.StartIndex);
        Assert.Equal(2, Assert.Single(_external.Queries).Limit);
    }

    [Fact]
    public async Task NoMatches_ReturnsAnEmptyList()
    {
        _external.Results.Clear();
        _libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());

        var response = Response(await RunAsync(new() { ["searchTerm"] = "zzz" }));

        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalRecordCount);
    }

    public static TheoryData<string, object> UnsupportedArguments => new()
    {
        { "nameStartsWith", "T" },
        { "nameLessThan", "T" },
        { "nameStartsWithOrGreater", "T" },
        { "personTypes", new[] { "Actor" } },
        { "excludePersonTypes", new[] { "Director" } },
        { "excludePersonTypes", new[] { "Artist", "Director" } },
        { "appearsInItemId", Guid.NewGuid() },
        { "isFavorite", false },
        { "parentId", Guid.NewGuid() },
        { "filters", new[] { ItemFilter.IsFavorite } },
    };

    [Theory]
    [MemberData(nameof(UnsupportedArguments))]
    public async Task FiltersTheProviderCantApply_GoToJellyfin(string name, object value)
    {
        var context = await RunAsync(new() { ["searchTerm"] = "t", [name] = value });

        Assert.True(_nextCalled);
        Assert.Null(context.Result);
        Assert.Empty(_external.Queries);
    }

    [Fact]
    public async Task WebClientRequest_WithoutMusicLibraries_IsAnsweredFromTheProvider()
    {
        var context = await RunAsync(WebClientRequest());

        Assert.False(_nextCalled);
        Assert.Equal(new[] { "Tom Hanks", "Tim Allen" }, Response(context).Items.Select(dto => dto.Name));
        Assert.Equal(new[] { BaseItemKind.Person }, Assert.Single(_external.Queries).IncludeItemTypes);
    }

    [Fact]
    public async Task WebClientRequest_WithAMusicLibrary_GoesToJellyfin()
    {
        _libraries.Add(new VirtualFolderInfo { Name = "Music", CollectionType = CollectionTypeOptions.music });

        var context = await RunAsync(WebClientRequest());

        Assert.True(_nextCalled);
        Assert.Null(context.Result);
        Assert.Empty(_external.Queries);
    }

    // Jellyfin also credits artists on music videos, on audio files in mixed-content libraries, and on books.
    [Theory]
    [InlineData(CollectionTypeOptions.musicvideos)]
    [InlineData(CollectionTypeOptions.books)]
    [InlineData(null)]
    public async Task WebClientRequest_WithAnotherLibraryThatCreditsArtists_GoesToJellyfin(CollectionTypeOptions? collectionType)
    {
        _libraries.Add(new VirtualFolderInfo { Name = "Other", CollectionType = collectionType });

        await RunAsync(WebClientRequest());

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task ExcludedArtistTypes_AreMatchedIgnoringCase()
    {
        var request = WebClientRequest();
        request["excludePersonTypes"] = new[] { "artist", "ALBUMARTIST" };

        await RunAsync(request);

        Assert.False(_nextCalled);
    }

    [Fact]
    public async Task LibraryCheck_IsCachedForAMinute()
    {
        var filter = CreateFilter();
        await RunAsync(WebClientRequest(), filter: filter);

        _libraries.Add(new VirtualFolderInfo { Name = "Music", CollectionType = CollectionTypeOptions.music });
        await RunAsync(WebClientRequest(), filter: filter);
        Assert.False(_nextCalled);
        _libraryManager.Received(1).GetVirtualFolders();

        _time.Advance(TimeSpan.FromSeconds(61));
        await RunAsync(WebClientRequest(), filter: filter);
        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task DebugLogging_SaysWhoServedEachSearch()
    {
        _configuration.DebugLogging = true;

        await RunAsync(WebClientRequest("tom"));
        await RunAsync(new() { ["searchTerm"] = "tim", ["nameStartsWith"] = "T" });
        _alice.SetPermission(PermissionKind.EnableAllFolders, false);
        await RunAsync(new() { ["searchTerm"] = "kid" });

        Assert.Equal(3, _log.Messages.Count);
        Assert.Matches(@"^People search for 'tom' served by external: 2 results in \d+ ms$", _log.Messages[0]);
        Assert.Equal("People search for 'tim' passed to Jellyfin: unsupported parameter nameStartsWith", _log.Messages[1]);
        Assert.Equal("People search for 'kid' passed to Jellyfin: restricted user", _log.Messages[2]);
    }

    [Fact]
    public async Task DebugLogging_ReportsAnUnavailableProvider()
    {
        _configuration.DebugLogging = true;
        _external.CanSearchQueries = false;

        await RunAsync(WebClientRequest("tom"));

        Assert.Equal("People search for 'tom' passed to Jellyfin: provider unavailable", Assert.Single(_log.Messages));
    }

    [Fact]
    public async Task WithoutDebugLogging_NothingIsLogged()
    {
        await RunAsync(WebClientRequest());
        await RunAsync(new() { ["searchTerm"] = "t", ["nameStartsWith"] = "T" });

        Assert.Empty(_log.Messages);
    }

    [Fact]
    public async Task EmptyArgumentsOfTheSameNames_AreIgnored()
    {
        var context = await RunAsync(new()
        {
            ["searchTerm"] = "t",
            ["nameStartsWith"] = null,
            ["personTypes"] = Array.Empty<string>(),
            ["excludePersonTypes"] = Array.Empty<string>(),
            ["filters"] = Array.Empty<ItemFilter>(),
        });

        Assert.False(_nextCalled);
        Assert.IsType<OkObjectResult>(context.Result);
    }

    [Fact]
    public async Task ListingWithoutSearchTerm_GoesToJellyfin()
    {
        await RunAsync(new() { ["searchTerm"] = null, ["limit"] = 100 });

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task RestrictedUser_GoesToJellyfin()
    {
        _alice.SetPermission(PermissionKind.EnableAllFolders, false);

        await RunAsync(new() { ["searchTerm"] = "t" });

        Assert.True(_nextCalled);
        Assert.Empty(_external.Queries);
    }

    [Fact]
    public async Task ParentalRatingLimit_GoesToJellyfin()
    {
        _alice.MaxParentalRatingScore = 10;

        await RunAsync(new() { ["searchTerm"] = "t" });

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task AnotherUsersId_IsForbiddenForNonAdministrators()
    {
        await Assert.ThrowsAsync<SecurityException>(() => RunAsync(new() { ["searchTerm"] = "t", ["userId"] = _bob.Id }));
        Assert.False(_nextCalled);
    }

    [Fact]
    public async Task AnotherUsersId_IsAllowedForAdministrators()
    {
        await RunAsync(new() { ["searchTerm"] = "t", ["userId"] = _bob.Id }, Principal(_alice, isAdministrator: true));

        Assert.False(_nextCalled);
        Assert.Equal(_bob.Id, Assert.Single(_external.Queries).UserId);
    }

    [Fact]
    public async Task ProviderFailure_GoesToJellyfin()
    {
        _external.Throws = new InvalidOperationException("down");

        await RunAsync(new() { ["searchTerm"] = "t" });

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task UnexpectedException_GoesToJellyfin()
    {
        _libraryManager.GetItemById(_tom.Id).Throws(new InvalidOperationException("database locked"));

        var context = await RunAsync(new() { ["searchTerm"] = "t" });

        Assert.True(_nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Unauthenticated_GoesToJellyfin()
    {
        await RunAsync(new() { ["searchTerm"] = "t" }, new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task OtherActions_AreLeftAlone()
    {
        await RunAsync(new() { ["searchTerm"] = "t" }, controller: "Items", action: "GetItems");

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task PeopleSearchSwitchedOff_GoesToJellyfin()
    {
        _configuration.AcceleratePeopleSearch = false;

        await RunAsync(new() { ["searchTerm"] = "t" });

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task SearchManagerNotReplaced_GoesToJellyfin()
    {
        await RunAsync(new() { ["searchTerm"] = "t" }, filter: CreateFilter(new FakeJellyfinSearchManager()));

        Assert.True(_nextCalled);
    }
}
