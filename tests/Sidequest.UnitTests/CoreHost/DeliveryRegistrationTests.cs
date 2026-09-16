using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sidequest.Application;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Infrastructure;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.CoreHost;

/// <summary>Verifies host delivery composition, scope isolation and configuration without connecting to SQL or providers.</summary>
public sealed class DeliveryRegistrationTests
{
    /// <summary>Resolves real implementations with validated dependency lifetimes and keeps incomplete background processing inactive.</summary>
    [Fact]
    public void ResolvesRealServicesInIndependentScopesWithoutStartingWorker()
    {
        var configuration = Configuration();
        var services = Services(configuration);
        Assert.Same(services, services.AddSidequestDelivery(configuration));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var notifications = first.ServiceProvider.GetRequiredService<INotificationService>();
        Assert.IsType<NotificationService>(notifications);
        Assert.Same(notifications, first.ServiceProvider.GetRequiredService<INotificationService>());
        Assert.NotSame(notifications, second.ServiceProvider.GetRequiredService<INotificationService>());
        Assert.IsType<DurableWorkRunner>(first.ServiceProvider.GetRequiredService<DurableWorkRunner>());
        var execution = first.ServiceProvider.GetRequiredService<WorkExecutionContext>();
        Assert.Same(execution, first.ServiceProvider.GetRequiredService<WorkExecutionContext>());
        Assert.NotSame(execution, second.ServiceProvider.GetRequiredService<WorkExecutionContext>());
        var renderer = first.ServiceProvider.GetRequiredService<IRecipientCalendarRenderer>();
        Assert.IsType<RecipientCalendarRenderer>(renderer);
        Assert.Same(renderer, second.ServiceProvider.GetRequiredService<IRecipientCalendarRenderer>());
        Assert.IsType<AcsEmailGateway>(provider.GetRequiredService<IEmailGateway>());
        var handlers = first.ServiceProvider.GetServices<IBackgroundWorkHandler>().ToArray();
        Assert.Equal(2, handlers.Length);
        Assert.IsType<ChangeOutboxHandler>(Assert.Single(handlers, handler => handler.WorkType == WorkTypes.Change));
        Assert.IsType<ReminderWorkHandler>(Assert.Single(handlers, handler => handler.WorkType == WorkTypes.Reminder));
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    /// <summary>Preserves explicit configuration and inclusive operational bounds rather than silently replacing them with defaults.</summary>
    /// <param name="upper">Whether to use upper rather than lower supported worker bounds.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindsEmailAndWorkerConfigurationAtSupportedBounds(bool upper)
    {
        var configuration = Configuration(new()
        {
            ["Delivery:Email:SenderAddress"] = "organizer@example.test",
            ["Delivery:Email:Endpoint"] = "https://example.communication.azure.com",
            ["Delivery:Email:ManagedIdentityClientId"] = "configured-identity",
            ["Delivery:Email:SubmissionTimeout"] = upper ? "00:01:30" : "00:00:00.0000001",
            ["Delivery:Work:PollInterval"] = upper ? "00:00:30" : "00:00:01",
            ["Delivery:Work:LeaseDuration"] = upper ? "00:02:00" : "00:00:30",
            ["Delivery:Work:ReminderLateness"] = upper ? "00:02:00" : "00:00:00.0000001",
            ["Delivery:Work:Concurrency"] = upper ? "16" : "1"
        });
        var services = new ServiceCollection();
        services.AddSidequestDelivery(configuration);
        using var provider = services.BuildServiceProvider();
        var email = provider.GetRequiredService<EmailDeliveryOptions>();
        Assert.Equal("organizer@example.test", email.SenderAddress);
        Assert.Equal("https://example.communication.azure.com", email.Endpoint);
        Assert.Equal("configured-identity", email.ManagedIdentityClientId);
        Assert.Null(email.ConnectionString);
        Assert.Equal(upper ? TimeSpan.FromSeconds(90) : TimeSpan.FromTicks(1), email.SubmissionTimeout);
        var work = provider.GetRequiredService<DurableWorkOptions>();
        Assert.Equal(TimeSpan.FromSeconds(upper ? 30 : 1), work.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(upper ? 120 : 30), work.LeaseDuration);
        Assert.Equal(upper ? TimeSpan.FromMinutes(2) : TimeSpan.FromTicks(1), work.ReminderLateness);
        Assert.Equal(upper ? 16 : 1, work.Concurrency);
    }

    /// <summary>Rejects each invalid operational setting before adding a partially configured delivery service graph.</summary>
    /// <param name="key">Configuration key to invalidate.</param>
    /// <param name="value">Out-of-range or malformed value.</param>
    [Theory]
    [InlineData("Delivery:Work:PollInterval", "00:00:00.9999999")]
    [InlineData("Delivery:Work:PollInterval", "00:00:30.0000001")]
    [InlineData("Delivery:Work:LeaseDuration", "00:00:29.9999999")]
    [InlineData("Delivery:Work:LeaseDuration", "00:02:00.0000001")]
    [InlineData("Delivery:Work:ReminderLateness", "00:00:00")]
    [InlineData("Delivery:Work:ReminderLateness", "00:02:00.0000001")]
    [InlineData("Delivery:Work:Concurrency", "0")]
    [InlineData("Delivery:Work:Concurrency", "17")]
    [InlineData("Delivery:Work:Concurrency", "invalid")]
    [InlineData("Delivery:Email:SubmissionTimeout", "00:00:00")]
    [InlineData("Delivery:Email:SubmissionTimeout", "00:01:30.0000001")]
    [InlineData("Delivery:Email:SubmissionTimeout", "invalid")]
    public void InvalidSettingsFailBeforeServiceRegistration(string key, string value)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new() { [key] = value });
        var failure = Record.Exception(() => services.AddSidequestDelivery(configuration));
        if (value == "invalid" || key.StartsWith("Delivery:Email:", StringComparison.Ordinal))
            Assert.IsType<InvalidOperationException>(failure);
        else
            Assert.IsType<ArgumentOutOfRangeException>(failure);
        Assert.Empty(services);
    }

    /// <summary>Retains documented defaults and fails explicitly when email is invoked without deployment credentials.</summary>
    /// <returns>A task completing after the missing-provider failure is inspected.</returns>
    [Fact]
    public async Task DefaultsDoNotPretendProviderIsConfigured()
    {
        var services = Services(Configuration());
        services.AddSidequestDelivery(Configuration());
        using var provider = services.BuildServiceProvider();
        var email = provider.GetRequiredService<EmailDeliveryOptions>();
        Assert.Empty(email.SenderAddress);
        Assert.Null(email.ConnectionString);
        Assert.Null(email.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(60), email.SubmissionTimeout);
        var work = provider.GetRequiredService<DurableWorkOptions>();
        Assert.Equal(TimeSpan.FromSeconds(10), work.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(90), work.LeaseDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), work.ReminderLateness);
        Assert.Equal(4, work.Concurrency);
        var gateway = provider.GetRequiredService<IEmailGateway>();
        var failure = await Assert.ThrowsAsync<DeliveryTransportException>(() => gateway.SendAsync(
            new("recipient@example.test", "subject", "html", "text", "test-key")));
        Assert.Equal(TransportOutcome.Permanent, failure.Outcome);
    }

    /// <summary>Copies secret-store configuration without constructing a provider client or requiring a usable credential at registration.</summary>
    [Fact]
    public void BindsProtectedConnectionStringWithoutConstructingProvider()
    {
        var services = new ServiceCollection();
        services.AddSidequestDelivery(Configuration(new()
        {
            ["Delivery:Email:ConnectionString"] = "test-only-configuration-value"
        }));
        var settings = Assert.IsType<EmailDeliveryOptions>(
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(EmailDeliveryOptions)).ImplementationInstance);
        Assert.Equal("test-only-configuration-value", settings.ConnectionString);
        var gateway = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IEmailGateway));
        Assert.Null(gateway.ImplementationInstance);
        Assert.NotNull(gateway.ImplementationFactory);
        var provider = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(AcsEmailGateway));
        Assert.Null(provider.ImplementationInstance);
        Assert.Equal(typeof(AcsEmailGateway), provider.ImplementationType);
    }

    /// <summary>Rejects missing host inputs with exact parameter diagnostics before modifying service registrations.</summary>
    [Fact]
    public void NullInputsFailBeforeRegistration()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(
            () => DeliveryRegistration.AddSidequestDelivery(null!, Configuration())).ParamName);
        var services = new ServiceCollection();
        Assert.Equal("configuration", Assert.Throws<ArgumentNullException>(
            () => services.AddSidequestDelivery(null!)).ParamName);
        Assert.Empty(services);
    }

    private static IConfiguration Configuration(Dictionary<string, string?>? values = null)
    {
        values ??= [];
        values["ConnectionStrings:Sidequest"] = "Server=invalid.example.test;Database=CompositionOnly;Integrated Security=true";
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceCollection Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentUser, AnonymousUser>();
        services.AddSidequestApplication();
        services.AddSidequestInfrastructure(configuration);
        return services;
    }

    private sealed class AnonymousUser : ICurrentUser
    {
        /// <inheritdoc/>
        public ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<UserIdentity?>(null);
        }
    }
}
