using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SearchAccelerator.Configuration;
using Jellyfin.Plugin.SearchAccelerator.Search;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SearchAccelerator.Api;

/// <summary>
/// Answers people searches (/Persons?searchTerm=) from the search providers. Jellyfin's PersonsController
/// never asks them; it scans the people table with a SQL "contains" instead. Runs inside Jellyfin's MVC
/// pipeline, after Jellyfin's [Authorize] has already rejected unauthenticated requests.
/// </summary>
public sealed class PeopleSearchActionFilter : IAsyncActionFilter
{
    // Values from Jellyfin.Api's InternalClaimTypes and UserRoles, which plugins can't reference.
    private const string UserIdClaimType = "Jellyfin-UserId";
    private const string AdministratorRole = "Administrator";

    // Jellyfin's own default when a client asks for every match.
    private const int DefaultSearchLimit = 100;
    private const int MaxSearchLimit = 1000;

    private static readonly TimeSpan _libraryCheckInterval = TimeSpan.FromMinutes(1);

    // Filters the search providers can't apply. Requests using any of them go to Jellyfin.
    // excludePersonTypes is handled separately: Jellyfin Web sends it on every People search.
    private static readonly string[] _unsupportedArguments =
    {
        "nameStartsWith",
        "nameLessThan",
        "nameStartsWithOrGreater",
        "personTypes",
        "appearsInItemId",
        "isFavorite",
        "parentId",
    };

    // Jellyfin Web always sends excludePersonTypes=Artist,AlbumArtist.
    private static readonly string[] _artistPersonTypes = { "Artist", "AlbumArtist" };

    private readonly ISearchManager _searchManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly ILogger<PeopleSearchActionFilter> _logger;
    private readonly Func<PluginConfiguration?> _getConfiguration;
    private readonly TimeProvider _timeProvider;
    private readonly object _libraryCheckLock = new();
    private bool _hasArtistCredits;
    private DateTimeOffset _libraryCheckedAt = DateTimeOffset.MinValue;

    public PeopleSearchActionFilter(
        ISearchManager searchManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        ILogger<PeopleSearchActionFilter> logger)
        : this(searchManager, userManager, libraryManager, dtoService, logger, () => Plugin.Instance?.Configuration, TimeProvider.System)
    {
    }

    internal PeopleSearchActionFilter(
        ISearchManager searchManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService,
        ILogger<PeopleSearchActionFilter> logger,
        Func<PluginConfiguration?> getConfiguration,
        TimeProvider timeProvider)
    {
        _searchManager = searchManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _logger = logger;
        _getConfiguration = getConfiguration;
        _timeProvider = timeProvider;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = _getConfiguration();
        if (configuration is null
            || !configuration.Enabled
            || !configuration.AcceleratePeopleSearch
            || _searchManager is not AcceleratedSearchManager searchManager
            || context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerName != "Persons"
            || action.ActionName != "GetPersons"
            || context.HttpContext.User.Identity?.IsAuthenticated != true
            || GetArgument<string>(context.ActionArguments, "searchTerm") is not { } searchTerm
            || string.IsNullOrWhiteSpace(searchTerm))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        if (GetUnsupportedReason(context.ActionArguments) is { } unsupported)
        {
            LogPassThrough(configuration, searchTerm, unsupported);
            await next().ConfigureAwait(false);
            return;
        }

        // Outside the try: a forbidden request must get the 403, not Jellyfin's answer.
        var userId = ResolveRequestUserId(context.HttpContext.User, GetValue<Guid>(context.ActionArguments, "userId"));

        QueryResult<BaseItemDto>? result;
        string? reason;
        try
        {
            (result, reason) = await TrySearchAsync(context, searchManager, configuration, searchTerm, userId).ConfigureAwait(false);
        }
        catch (Exception ex) when (!context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            _logger.LogError(ex, "People search failed; using Jellyfin's");
            (result, reason) = (null, "error");
        }

        if (result is null)
        {
            LogPassThrough(configuration, searchTerm, reason ?? "provider unavailable");
            await next().ConfigureAwait(false);
            return;
        }

        if (configuration.DebugLogging)
        {
            _logger.LogInformation(
                "People search for '{SearchTerm}' served by external: {Count} results in {Elapsed} ms",
                searchTerm,
                result.TotalRecordCount,
                stopwatch.ElapsedMilliseconds);
        }

        context.Result = new OkObjectResult(result);
    }

    /// <summary>
    /// Same rule as Jellyfin's RequestHelpers.GetUserId: no user id means the caller's own;
    /// another user's id requires an administrator. Jellyfin's ExceptionMiddleware turns its own
    /// MediaBrowser.Controller.Net.SecurityException (not System.Security's) into the standard 403.
    /// </summary>
    internal static Guid ResolveRequestUserId(ClaimsPrincipal principal, Guid? requestedUserId)
    {
        var authenticatedUserId = GetAuthenticatedUserId(principal);
        if (requestedUserId is null || requestedUserId.Value.Equals(Guid.Empty))
        {
            return authenticatedUserId;
        }

        if (!requestedUserId.Value.Equals(authenticatedUserId) && !principal.IsInRole(AdministratorRole))
        {
            throw new SecurityException("Forbidden");
        }

        return requestedUserId.Value;
    }

    /// <returns>Why the provider can't answer this request, or null if it can.</returns>
    internal string? GetUnsupportedReason(IDictionary<string, object?> arguments)
    {
        foreach (var name in _unsupportedArguments)
        {
            if (arguments.TryGetValue(name, out var value) && IsSet(value))
            {
                return $"unsupported parameter {name}";
            }
        }

        if (GetArgument<ItemFilter[]>(arguments, "filters") is { } filters && filters.Contains(ItemFilter.IsFavorite))
        {
            return "unsupported parameter filters=IsFavorite";
        }

        // The provider can't exclude by role. Leaving out Artist and AlbumArtist changes nothing when no one in
        // the library holds those roles.
        if (GetArgument<string[]>(arguments, "excludePersonTypes") is { Length: > 0 } excludedTypes)
        {
            if (!excludedTypes.All(type => _artistPersonTypes.Contains(type, StringComparer.OrdinalIgnoreCase)))
            {
                return "unsupported parameter excludePersonTypes";
            }

            if (HasArtistCredits())
            {
                return "unsupported parameter excludePersonTypes (a library may credit artists)";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether any library can hold Artist or AlbumArtist credits. Jellyfin 12.3 creates them for audio files
    /// (AudioFileProber), music albums, and books (OpfReader), so music, music video, book and mixed-content
    /// libraries all count. Checked at most once a minute.
    /// </summary>
    private bool HasArtistCredits()
    {
        lock (_libraryCheckLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _libraryCheckedAt >= _libraryCheckInterval)
            {
                _hasArtistCredits = _libraryManager.GetVirtualFolders().Any(folder => folder.CollectionType
                    is null
                    or CollectionTypeOptions.music
                    or CollectionTypeOptions.musicvideos
                    or CollectionTypeOptions.books);
                _libraryCheckedAt = now;
            }

            return _hasArtistCredits;
        }
    }

    /// <returns>The response, or null and the reason to let Jellyfin answer.</returns>
    private async Task<(QueryResult<BaseItemDto>? Result, string? Reason)> TrySearchAsync(
        ActionExecutingContext context,
        AcceleratedSearchManager searchManager,
        PluginConfiguration configuration,
        string searchTerm,
        Guid userId)
    {
        var arguments = context.ActionArguments;

        // People aren't owned by a library; which of them a restricted user may see follows from the items
        // they're credited on, which only Jellyfin's own query checks. Requests without a user (an API key)
        // go to Jellyfin too.
        var user = userId.Equals(Guid.Empty) ? null : _userManager.GetUserById(userId);
        if (user is null)
        {
            return (null, "no user");
        }

        if (user.HasContentRestrictions())
        {
            return (null, "restricted user");
        }

        var startIndex = Math.Max(0, GetValue<int>(arguments, "startIndex") ?? 0);
        var limit = GetValue<int>(arguments, "limit") is { } requestedLimit && requestedLimit > 0 ? requestedLimit : (int?)null;
        var searchLimit = Math.Min(MaxSearchLimit, startIndex + (limit ?? DefaultSearchLimit));

        var hits = await searchManager.TryGetExternalResultsAsync(
            new SearchProviderQuery
            {
                SearchTerm = searchTerm,
                UserId = userId,
                IncludeItemTypes = new[] { BaseItemKind.Person },
                Limit = searchLimit,
            },
            configuration,
            context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (hits is null)
        {
            return (null, "provider unavailable");
        }

        var people = hits
            .Select(hit => _libraryManager.GetItemById(hit.ItemId))
            .OfType<Person>()
            .ToList();

        var dtoOptions = BuildDtoOptions(arguments);
        var page = people
            .Skip(startIndex)
            .Take(limit ?? int.MaxValue)
            .Select(person => _dtoService.GetItemByNameDto(person, dtoOptions, null, user))
            .ToArray();

        return (new QueryResult<BaseItemDto>(startIndex, people.Count, page), null);
    }

    private void LogPassThrough(PluginConfiguration configuration, string searchTerm, string reason)
    {
        if (configuration.DebugLogging)
        {
            _logger.LogInformation("People search for '{SearchTerm}' passed to Jellyfin: {Reason}", searchTerm, reason);
        }
    }

    // Same as Jellyfin's DtoExtensions.AddAdditionalDtoOptions.
    private static DtoOptions BuildDtoOptions(IDictionary<string, object?> arguments)
    {
        var dtoOptions = new DtoOptions
        {
            Fields = GetArgument<ItemFields[]>(arguments, "fields") ?? Array.Empty<ItemFields>(),
            EnableImages = GetValue<bool>(arguments, "enableImages") ?? true,
        };

        if (GetValue<int>(arguments, "imageTypeLimit") is { } imageTypeLimit)
        {
            dtoOptions.ImageTypeLimit = imageTypeLimit;
        }

        if (GetValue<bool>(arguments, "enableUserData") is { } enableUserData)
        {
            dtoOptions.EnableUserData = enableUserData;
        }

        if (GetArgument<ImageType[]>(arguments, "enableImageTypes") is { Length: > 0 } enableImageTypes)
        {
            dtoOptions.ImageTypes = enableImageTypes;
        }

        return dtoOptions;
    }

    private static bool IsSet(object? value) => value switch
    {
        null => false,
        string text => !string.IsNullOrEmpty(text),
        Array array => array.Length > 0,
        Guid guid => !guid.Equals(Guid.Empty),
        _ => true,
    };

    private static Guid GetAuthenticatedUserId(ClaimsPrincipal principal)
    {
        var value = principal.Claims
            .FirstOrDefault(claim => claim.Type.Equals(UserIdClaimType, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return string.IsNullOrEmpty(value) ? Guid.Empty : Guid.Parse(value);
    }

    private static T? GetArgument<T>(IDictionary<string, object?> arguments, string name)
        where T : class
        => arguments.TryGetValue(name, out var value) ? value as T : null;

    private static TValue? GetValue<TValue>(IDictionary<string, object?> arguments, string name)
        where TValue : struct
        => arguments.TryGetValue(name, out var value) && value is TValue typed ? typed : null;
}
