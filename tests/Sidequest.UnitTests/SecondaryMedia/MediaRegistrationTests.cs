using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Media;
using Sidequest.Application.Media.Implementation;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Media;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.SecondaryMedia;

/// <summary>Verifies media composition resolves real implementations without contacting SQL or an unconfigured provider.</summary>
public sealed class MediaRegistrationTests
{
    /// <summary>Missing deployment configuration permits composition but never successful requested storage operations.</summary>
    /// <returns>Completion after real services and one cleanup handler resolve, followed by explicit storage failure.</returns>
    [Fact]
    public async Task Registration_ResolvesWithoutProviderIoAndOperationsFailExplicitly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISidequestDbContextFactory, RejectDatabase>();
        services.AddScoped<ICurrentUser, NoIdentity>();
        services.AddScoped<IResourceAccess, ResourceAccess>();
        services.AddScoped<IChangeWriter, ChangeWriter>();
        services.AddScoped<IQuestEventLifecycle, QuestEventLifecycle>();
        services.AddScoped<IEventLifecycleReconciler, EventLifecycleReconciler>();
        services.AddSidequestMedia(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        Assert.IsType<MediaService>(scope.ServiceProvider.GetRequiredService<IMediaService>());
        Assert.IsType<SkiaImageSanitizer>(scope.ServiceProvider.GetRequiredService<IImageSanitizer>());
        var handler = Assert.Single(scope.ServiceProvider.GetServices<IBackgroundWorkHandler>());
        Assert.IsType<MediaCleanupHandler>(handler);
        Assert.Equal(WorkTypes.MediaCleanup, handler.WorkType);
        var storage = Assert.IsType<AzurePrivateMediaStorage>(scope.ServiceProvider.GetRequiredService<IPrivateMediaStorage>());
        var failure = await Assert.ThrowsAsync<DomainException>(() => storage.DeleteIfExistsAsync($"covers/{Guid.NewGuid():N}.png"));
        Assert.Equal(ErrorCode.DependencyUnavailable, failure.Code);
    }

    private sealed class RejectDatabase : ISidequestDbContextFactory
    {
        /// <inheritdoc />
        public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Dependency resolution must not contact SQL.");
    }

    private sealed class NoIdentity : ICurrentUser
    {
        /// <inheritdoc />
        public ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<UserIdentity?>(null);
    }
}
