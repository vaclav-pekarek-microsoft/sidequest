using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.CoreComposition;

/// <summary>Checks startup completeness against real registrations without starting any polling service.</summary>
public sealed class WorkHandlerStartupCheckTests
{
    /// <summary>Validates all five actual production handlers in an isolated scope and shuts down without owning work.</summary>
    /// <returns>A task completing after inert startup and shutdown validation.</returns>
    [Fact]
    public async Task CompleteProductionGraph_StartupCheckSucceeds_WithoutStartingWorkers()
    {
        await using var provider = CoreWorkflowRegistrationTests.Services().BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var check = new WorkHandlerStartupCheck(provider.GetRequiredService<IServiceScopeFactory>());
        await check.StartAsync(CancellationToken.None);
        await check.StopAsync(CancellationToken.None);
        using var scope = provider.CreateScope();
        Assert.Equal(5, scope.ServiceProvider.GetServices<IBackgroundWorkHandler>().Select(x => x.WorkType).Distinct().Count());
        Assert.Null(scope.ServiceProvider.GetRequiredService<Sidequest.Infrastructure.Background.WorkExecutionContext>().Lease);
    }

    /// <summary>Rejects missing, duplicate and unsupported mappings, including incorrect same-count replacement graphs.</summary>
    /// <param name="malformation">Descriptor mutation applied to the real complete graph.</param>
    /// <returns>A task completing after exact startup failure inspection.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unsupported-addition")]
    [InlineData("unsupported-replacement")]
    [InlineData("same-count-duplicate")]
    public async Task MalformedHandlerGraph_StartupCheckRejectsExactMappingViolations(string malformation)
    {
        var services = CoreWorkflowRegistrationTests.Services();
        var original = Assert.Single(services, x => x.ServiceType == typeof(IBackgroundWorkHandler) &&
            x.ImplementationType == typeof(QuestCompletionHandler));
        if (malformation is "missing" or "unsupported-replacement" or "same-count-duplicate")
            Assert.True(services.Remove(original));
        if (malformation == "duplicate")
            services.Add(original);
        if (malformation.StartsWith("unsupported", StringComparison.Ordinal))
            services.AddScoped<IBackgroundWorkHandler>(_ => new UnsupportedHandler());
        if (malformation == "same-count-duplicate")
            services.Add(Assert.Single(services, x => x.ServiceType == typeof(IBackgroundWorkHandler) &&
                x.ImplementationType == typeof(Sidequest.Application.Events.Implementation.EventCompletionHandler)));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var check = new WorkHandlerStartupCheck(provider.GetRequiredService<IServiceScopeFactory>());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));
        Assert.Equal("Every supported durable work type must have exactly one registered handler.", error.Message);
        await check.StopAsync(CancellationToken.None);
    }

    /// <summary>Observes no handler resolution when startup is precancelled and no work when shutdown is cancelled.</summary>
    /// <returns>A task completing after cancellation and resolution-counter assertions.</returns>
    [Fact]
    public async Task PrecancelledStartup_DoesNotResolveHandlers_AndStopDoesNoWork()
    {
        var services = CoreWorkflowRegistrationTests.Services();
        var resolutions = 0;
        services.AddScoped<IBackgroundWorkHandler>(_ =>
        {
            resolutions++;
            throw new InvalidOperationException("Cancelled startup resolved a handler.");
        });
        await using var provider = services.BuildServiceProvider();
        var check = new WorkHandlerStartupCheck(provider.GetRequiredService<IServiceScopeFactory>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => check.StartAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await check.StopAsync(cancellation.Token);
        Assert.Equal(0, resolutions);
    }

    /// <summary>Uses the host's explicit Media requirement to validate six handlers without executing work or retaining the validation scope.</summary>
    /// <returns>A task completing after actual hosted-service resolution, inert validation and disposal assertions.</returns>
    [Fact]
    public async Task ConfiguredMedia_RequiresExactlyOneHandler_WithoutExecutingWork()
    {
        var services = CoreWorkflowRegistrationTests.Services();
        services.AddSingleton(new WorkHandlerRequirements(RequireMediaCleanup: true));
        var executions = 0;
        var disposals = 0;
        services.AddScoped<IBackgroundWorkHandler>(_ => new MediaHandler(() => executions++, () => disposals++));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var check = Assert.Single(provider.GetServices<IHostedService>().OfType<WorkHandlerStartupCheck>());

        await check.StartAsync(CancellationToken.None);

        Assert.Equal(0, executions);
        Assert.Equal(1, disposals);
        await check.StopAsync(new CancellationToken(canceled: true));
        Assert.Equal(0, executions);
        Assert.Equal(1, disposals);
    }

    /// <summary>Rejects a missing or duplicate enabled Media handler and an undeclared Media handler in a core-only host.</summary>
    /// <param name="enabled">Whether host composition explicitly requires Media cleanup.</param>
    /// <param name="handlerCount">Number of Media handlers registered in addition to the real five-handler core graph.</param>
    /// <param name="replaceQuestHandler">Whether a duplicate Media handler replaces a missing core handler while preserving the expected total count.</param>
    /// <returns>A task completing after exact startup rejection and zero-execution assertions.</returns>
    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 2, true)]
    public async Task ConfiguredMedia_MissingDuplicateOrUndeclaredHandler_RejectsStartup(bool enabled, int handlerCount, bool replaceQuestHandler)
    {
        var services = CoreWorkflowRegistrationTests.Services();
        services.AddSingleton(new WorkHandlerRequirements(enabled));
        if (replaceQuestHandler)
            Assert.True(services.Remove(Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IBackgroundWorkHandler) &&
                descriptor.ImplementationType == typeof(QuestCompletionHandler))));
        var executions = 0;
        var disposals = 0;
        for (var index = 0; index < handlerCount; index++)
            services.AddScoped<IBackgroundWorkHandler>(_ => new MediaHandler(() => executions++, () => disposals++));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var check = Assert.Single(provider.GetServices<IHostedService>().OfType<WorkHandlerStartupCheck>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

        Assert.Equal("Every supported durable work type must have exactly one registered handler.", error.Message);
        Assert.Equal(0, executions);
        Assert.Equal(handlerCount, disposals);
    }

    private sealed class MediaHandler(Action execute, Action dispose) : IBackgroundWorkHandler, IDisposable
    {
        /// <inheritdoc />
        public string WorkType => WorkTypes.MediaCleanup;

        /// <inheritdoc />
        public Task ExecuteAsync(Guid workId, CancellationToken cancellationToken)
        {
            execute();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose() => dispose();
    }

    private sealed class UnsupportedHandler : IBackgroundWorkHandler
    {
        /// <inheritdoc/>
        public string WorkType => "unsupported.v1";
        /// <inheritdoc/>
        public Task ExecuteAsync(Guid workId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Startup must never execute a handler.");
    }
}
