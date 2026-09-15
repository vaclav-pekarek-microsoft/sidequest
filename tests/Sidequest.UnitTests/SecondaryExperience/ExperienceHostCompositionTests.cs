using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Experience;
using Sidequest.Application.Media;
using Sidequest.Application.Media.Implementation;
using Sidequest.UnitTests.CoreComposition;
using Sidequest.Web.Experience;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies the actual feature registrations compose with the existing core graph and require the sixth durable handler before polling.</summary>
public sealed class ExperienceHostCompositionTests
{
    /// <summary>Real dashboard/media services resolve without provider I/O; omitting required Media fails startup rather than silently running five handlers.</summary>
    /// <param name="includeMedia">Whether the real Media feature and its cleanup handler are registered.</param>
    /// <returns>Completion after scoped-lifetime, exact handler-set and startup admission assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComposedExperienceRequiresExactlySixRealHandlers(bool includeMedia)
    {
        var services = CoreWorkflowRegistrationTests.Services();
        services.AddSidequestExperience();
        services.AddSingleton(new WorkHandlerRequirements(RequireMediaCleanup: true));
        if (includeMedia) services.AddSidequestMedia(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        Assert.IsType<DashboardService>(first.ServiceProvider.GetRequiredService<DashboardService>());
        var coordinator = first.ServiceProvider.GetRequiredService<ExperienceCoordinator>();
        Assert.Same(coordinator, first.ServiceProvider.GetRequiredService<ExperienceCoordinator>());
        Assert.NotSame(coordinator, second.ServiceProvider.GetRequiredService<ExperienceCoordinator>());
        var check = Assert.Single(provider.GetServices<IHostedService>().OfType<WorkHandlerStartupCheck>());
        if (!includeMedia)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));
            Assert.Contains("exactly one", failure.Message);
            return;
        }
        Assert.IsType<MediaService>(first.ServiceProvider.GetRequiredService<IMediaService>());
        var handlers = first.ServiceProvider.GetServices<IBackgroundWorkHandler>().ToArray();
        Assert.Equal(6, handlers.Length);
        Assert.Equal(new[] { WorkTypes.Change, WorkTypes.EventCompletion, WorkTypes.QuestCompletion,
            WorkTypes.BulkMembership, WorkTypes.Reminder, WorkTypes.MediaCleanup }.Order(), handlers.Select(h => h.WorkType).Order());
        Assert.IsType<MediaCleanupHandler>(Assert.Single(handlers, h => h.WorkType == WorkTypes.MediaCleanup));
        await check.StartAsync(CancellationToken.None);
    }
}
