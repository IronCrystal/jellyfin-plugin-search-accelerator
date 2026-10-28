using Jellyfin.Plugin.SearchAccelerator.Search;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SearchAccelerator.Tests;

public class PluginServiceRegistratorTests
{
    [Fact]
    public void JellyfinsSearchManager_IsReplacedByTheAcceleratedOne()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISearchManager, FakeJellyfinSearchManager>();
        services.AddSingleton(Substitute.For<ILibraryManager>());
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        Assert.True(PluginServiceRegistrator.ReplaceSearchManager(services));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<AcceleratedSearchManager>(provider.GetRequiredService<ISearchManager>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ISearchManager));
    }

    [Fact]
    public void UnknownRegistration_IsLeftAlone()
    {
        var services = new ServiceCollection();
        var existing = new FakeJellyfinSearchManager();
        services.AddSingleton<ISearchManager>(existing);

        Assert.False(PluginServiceRegistrator.ReplaceSearchManager(services));

        using var provider = services.BuildServiceProvider();
        Assert.Same(existing, provider.GetRequiredService<ISearchManager>());
    }
}
