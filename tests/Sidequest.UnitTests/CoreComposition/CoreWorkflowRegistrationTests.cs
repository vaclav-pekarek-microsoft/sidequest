using System.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Sidequest.Application;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Notifications;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Quests;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Delivery;
using Sidequest.Infrastructure.Directory;
using Sidequest.Infrastructure.Persistence;
using Sidequest.UnitTests.CoreEvents;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components.Events;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.CoreComposition;

/// <summary>Exercises the real host composition without starting workers, opening SQL, or contacting providers.</summary>
public sealed class CoreWorkflowRegistrationTests
{
    internal static readonly Guid Tenant = new("66de072a-19dc-43ba-92a0-22459eb0a0f7");
    internal static FoundationAuthenticationSettings Authentication => new(false, Tenant, "Workforce", null);

    /// <summary>Resolves the complete production graph and checks scoped aliases, singleton leaves and independent closed contexts.</summary>
    /// <param name="customClock">Whether application registration must preserve an explicitly supplied deterministic clock.</param>
    /// <returns>A task completing after disposable operation contexts have been inspected.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionGraph_ResolvesRealServicesWithExpectedLifetimes_WithoutIo(bool customClock)
    {
        var clock = new ControlledTimeProvider();
        var services = Services(clock: customClock ? clock : null);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        Assert.Same(customClock ? clock : TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        var expected = new Dictionary<Type, Type>
        {
            [typeof(IEventService)] = typeof(EventService),
            [typeof(IEventManagementQueries)] = typeof(EventService),
            [typeof(IQuestService)] = typeof(QuestService),
            [typeof(INotificationService)] = typeof(NotificationService),
            [typeof(IQuestEventLifecycle)] = typeof(QuestEventLifecycle),
            [typeof(IEventLifecycleReconciler)] = typeof(EventLifecycleReconciler),
            [typeof(IChangeWriter)] = typeof(ChangeWriter),
            [typeof(IResourceAccess)] = typeof(ResourceAccess),
            [typeof(ICurrentUser)] = typeof(CircuitCurrentUser),
            [typeof(ISidequestDbContextFactory)] = typeof(SidequestDbContextFactory),
            [typeof(SqlWorkQueue)] = typeof(SqlWorkQueue),
            [typeof(DurableWorkRunner)] = typeof(DurableWorkRunner),
            [typeof(DeliveryDispatcher)] = typeof(DeliveryDispatcher),
            [typeof(WorkExecutionContext)] = typeof(WorkExecutionContext)
        };
        foreach (var (contract, implementation) in expected)
        {
            var value = first.ServiceProvider.GetRequiredService(contract);
            Assert.IsType(implementation, value);
            Assert.Same(value, first.ServiceProvider.GetRequiredService(contract));
            Assert.NotSame(value, second.ServiceProvider.GetRequiredService(contract));
            Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, x => x.ServiceType == contract).Lifetime);
        }
        Assert.Same(first.ServiceProvider.GetRequiredService<IEventService>(),
            first.ServiceProvider.GetRequiredService<IEventManagementQueries>());
        Assert.Same(first.ServiceProvider.GetRequiredService<EventCircuitRevalidation>(),
            first.ServiceProvider.GetRequiredService<CircuitHandler>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<CircuitHandler>(),
            second.ServiceProvider.GetRequiredService<CircuitHandler>());
        Assert.IsType<WorkforceAuthenticationStateProvider>(first.ServiceProvider.GetRequiredService<AuthenticationStateProvider>());
        Assert.IsType<GraphDirectoryGateway>(first.ServiceProvider.GetRequiredService<IDirectoryGateway>());
        Assert.IsType<GraphClientCredentialTokenProvider>(first.ServiceProvider.GetRequiredService<IGraphAccessTokenProvider>());
        foreach (var contract in new[] { typeof(IRecipientCalendarRenderer), typeof(IEmailGateway),
            typeof(EmailDeliveryOptions), typeof(DurableWorkOptions), typeof(GraphDirectoryOptions),
            typeof(GraphClientCredentialOptions), typeof(EventOperationOptions), typeof(FoundationAuthenticationSettings) })
            Assert.Same(first.ServiceProvider.GetRequiredService(contract), second.ServiceProvider.GetRequiredService(contract));
        Assert.IsType<RecipientCalendarRenderer>(provider.GetRequiredService<IRecipientCalendarRenderer>());
        Assert.IsType<AcsEmailGateway>(provider.GetRequiredService<IEmailGateway>());
        var handlers = first.ServiceProvider.GetServices<IBackgroundWorkHandler>().ToArray();
        Assert.Equal(5, handlers.Length);
        Assert.IsType<ChangeOutboxHandler>(Assert.Single(handlers, x => x.WorkType == WorkTypes.Change));
        Assert.IsType<EventCompletionHandler>(Assert.Single(handlers, x => x.WorkType == WorkTypes.EventCompletion));
        Assert.IsType<QuestCompletionHandler>(Assert.Single(handlers, x => x.WorkType == WorkTypes.QuestCompletion));
        Assert.IsType<BulkMembershipHandler>(Assert.Single(handlers, x => x.WorkType == WorkTypes.BulkMembership));
        Assert.IsType<ReminderWorkHandler>(Assert.Single(handlers, x => x.WorkType == WorkTypes.Reminder));
        var otherHandlers = second.ServiceProvider.GetServices<IBackgroundWorkHandler>().ToArray();
        foreach (var handler in handlers)
            Assert.NotSame(handler, Assert.Single(otherHandlers, x => x.WorkType == handler.WorkType));
        Assert.Equal(new[] { typeof(WorkHandlerStartupCheck), typeof(DurableWorkHostedService) },
            services.Where(x => x.ServiceType == typeof(IHostedService)).Select(x => x.ImplementationType));
        var factory = first.ServiceProvider.GetRequiredService<ISidequestDbContextFactory>();
        await using var db1 = await factory.CreateAsync();
        await using var db2 = await factory.CreateAsync();
        Assert.NotSame(db1, db2);
        Assert.Equal(ConnectionState.Closed, Assert.IsType<SidequestDbContext>(db1).Database.GetDbConnection().State);
        Assert.Equal(ConnectionState.Closed, Assert.IsType<SidequestDbContext>(db2).Database.GetDbConnection().State);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            first.ServiceProvider.GetRequiredService<ICurrentUser>().GetIdentityAsync(cancelled.Token).AsTask());
        var execution = first.ServiceProvider.GetRequiredService<WorkExecutionContext>();
        execution.Lease = new(Guid.NewGuid(), "scheduled", WorkTypes.QuestCompletion, Guid.NewGuid(), 1);
        Assert.Null(second.ServiceProvider.GetRequiredService<WorkExecutionContext>().Lease);
        Assert.Equal(WorkTypes.QuestCompletion, execution.Lease.Type);
    }

    /// <summary>Binds only the documented nested sections and preserves both conservative defaults and explicit deployment values.</summary>
    /// <param name="configured">Whether exact nested sections contain overrides rather than misleading root keys only.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreSettings_BindDefaultsAndNondefaultValues_FromExactSections(bool configured)
    {
        var values = new Dictionary<string, string?>
        {
            ["Events:MaximumBulkRecipients"] = "999", ["Directory:TenantId"] = Guid.NewGuid().ToString(),
            ["Delivery:Concurrency"] = "15"
        };
        if (configured)
        {
            values["Events:Limits:MaximumBulkRecipients"] = "42";
            values["Events:Limits:BulkStartsPerHour"] = "7";
            values["Events:Limits:RequestsPerHour"] = "9";
            values["Events:Limits:InvitationsPerHour"] = "19";
            values["Events:Limits:DirectorySearchesPerMinute"] = "23";
            values["Directory:Graph:TenantId"] = Tenant.ToString();
            values["Directory:Graph:WorkforcePolicyApproved"] = "true";
            values["Directory:Graph:WorkforceExtension"] = "extension_test_workforce";
            values["Directory:Graph:WorkforceValue"] = "approved";
            values["Directory:Graph:MaximumRecipients"] = "321";
            values["Directory:Graph:MaximumPages"] = "17";
            values["Directory:Graph:MaximumThrottlingRetries"] = "2";
            values["Directory:Credentials:TenantId"] = Tenant.ToString();
            values["Directory:Credentials:ClientId"] = Tenant.ToString();
            values["Directory:Credentials:ClientSecret"] = "synthetic-test-only-not-a-credential";
            values["Delivery:Work:Concurrency"] = "3";
            values["Delivery:Work:PollInterval"] = "00:00:05";
            values["Delivery:Work:LeaseDuration"] = "00:01:00";
            values["Delivery:Work:ReminderLateness"] = "00:01:00";
            values["Delivery:Email:SenderAddress"] = "organizer@example.invalid";
            values["Delivery:Email:SubmissionTimeout"] = "00:00:20";
            values["Delivery:Email:Endpoint"] = "https://email.example.invalid";
            values["Delivery:Email:ManagedIdentityClientId"] = "synthetic-identity";
        }
        using var provider = Services(values).BuildServiceProvider();
        var limits = provider.GetRequiredService<EventOperationOptions>();
        Assert.Equal(configured ? 42 : 5000, limits.MaximumBulkRecipients);
        Assert.Equal(configured ? 7 : 5, limits.BulkStartsPerHour);
        Assert.Equal(configured ? 9 : 5, limits.RequestsPerHour);
        Assert.Equal(configured ? 19 : 1000, limits.InvitationsPerHour);
        Assert.Equal(configured ? 23 : 30, limits.DirectorySearchesPerMinute);
        var graph = provider.GetRequiredService<GraphDirectoryOptions>();
        Assert.Equal(configured ? Tenant : Guid.Empty, graph.TenantId);
        Assert.Equal(configured, graph.WorkforcePolicyApproved);
        Assert.Equal(configured ? "extension_test_workforce" : "", graph.WorkforceExtension);
        Assert.Equal(configured ? "approved" : "", graph.WorkforceValue);
        Assert.Equal(configured ? 321 : 5000, graph.MaximumRecipients);
        Assert.Equal(configured ? 17 : 1000, graph.MaximumPages);
        Assert.Equal(configured ? 2 : 3, graph.MaximumThrottlingRetries);
        var credentials = provider.GetRequiredService<GraphClientCredentialOptions>();
        Assert.Equal(configured ? Tenant : Guid.Empty, credentials.TenantId);
        Assert.Equal(configured ? Tenant : Guid.Empty, credentials.ClientId);
        Assert.Equal(configured ? "synthetic-test-only-not-a-credential" : "", credentials.ClientSecret);
        var work = provider.GetRequiredService<DurableWorkOptions>();
        Assert.Equal(configured ? 3 : 4, work.Concurrency);
        Assert.Equal(TimeSpan.FromSeconds(configured ? 5 : 10), work.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(configured ? 60 : 90), work.LeaseDuration);
        Assert.Equal(TimeSpan.FromMinutes(configured ? 1 : 2), work.ReminderLateness);
        var email = provider.GetRequiredService<EmailDeliveryOptions>();
        Assert.Equal(configured ? "organizer@example.invalid" : "", email.SenderAddress);
        Assert.Equal(TimeSpan.FromSeconds(configured ? 20 : 60), email.SubmissionTimeout);
        Assert.Null(email.ConnectionString);
        Assert.Equal(configured ? "https://email.example.invalid" : null, email.Endpoint);
        Assert.Equal(configured ? "synthetic-identity" : null, email.ManagedIdentityClientId);
    }

    /// <summary>Rejects adjacent invalid Event limits atomically and accepts the inclusive supported boundaries.</summary>
    /// <param name="key">Limit in the exact Events:Limits section.</param>
    /// <param name="value">Boundary input.</param>
    /// <param name="valid">Whether registration must accept the value.</param>
    [Theory]
    [InlineData("MaximumBulkRecipients", "0", false)]
    [InlineData("MaximumBulkRecipients", "1", true)]
    [InlineData("MaximumBulkRecipients", "100000", true)]
    [InlineData("MaximumBulkRecipients", "100001", false)]
    [InlineData("BulkStartsPerHour", "0", false)]
    [InlineData("BulkStartsPerHour", "1", true)]
    [InlineData("RequestsPerHour", "0", false)]
    [InlineData("RequestsPerHour", "1", true)]
    [InlineData("InvitationsPerHour", "0", false)]
    [InlineData("InvitationsPerHour", "1", true)]
    [InlineData("DirectorySearchesPerMinute", "0", false)]
    [InlineData("DirectorySearchesPerMinute", "1", true)]
    public void EventLimits_ValidateExactAndAdjacentBounds_WithoutPartialRegistration(string key, string value, bool valid)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new() { [$"Events:Limits:{key}"] = value });
        if (valid)
        {
            Assert.Same(services, services.AddSidequestCoreWorkflows(configuration, Authentication));
            Assert.Equal(3, services.Count(x => x.ServiceType == typeof(IBackgroundWorkHandler)));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => services.AddSidequestCoreWorkflows(configuration, Authentication));
            Assert.Empty(services);
        }
    }

    /// <summary>Rejects each independent tenant override and malformed binding before registering any core service.</summary>
    /// <param name="key">Configuration path with an invalid tenant or scalar.</param>
    /// <param name="value">Invalid value, or mismatch to select an independent tenant.</param>
    [Theory]
    [InlineData("Directory:Graph:TenantId", "mismatch")]
    [InlineData("Directory:Credentials:TenantId", "mismatch")]
    [InlineData("Directory:Graph:TenantId", "invalid")]
    [InlineData("Events:Limits:MaximumBulkRecipients", "invalid")]
    public void DirectoryTenants_RejectIndependentMismatch_WithoutPartialCoreRegistration(string key, string value)
    {
        var services = new ServiceCollection();
        var configuration = Configuration(new() { [key] = value == "mismatch" ? Guid.NewGuid().ToString() : value });
        Assert.Throws<InvalidOperationException>(() => services.AddSidequestCoreWorkflows(configuration, Authentication));
        Assert.Empty(services);
    }

    /// <summary>Executes real adapter preflight and proves invalid policy or absent credentials cannot reach HTTP.</summary>
    /// <param name="partition">Independent fail-closed policy or credential gate.</param>
    /// <returns>A task completing after the explicit dependency-unavailable rejection.</returns>
    [Theory]
    [InlineData("unapproved")]
    [InlineData("extension")]
    [InlineData("value")]
    [InlineData("tenant")]
    [InlineData("recipients")]
    [InlineData("pages")]
    [InlineData("retries")]
    [InlineData("credentials")]
    public async Task DirectoryPreflight_InvalidPolicyOrMissingCredentials_FailsBeforeHttp(string partition)
    {
        using var transport = new ControlledHttpHandler((_, _) => throw new InvalidOperationException("Unexpected HTTP."));
        var values = new Dictionary<string, string?>
        {
            ["Directory:Graph:TenantId"] = (partition == "tenant" ? Guid.Empty : Tenant).ToString(),
            ["Directory:Graph:WorkforcePolicyApproved"] = (partition != "unapproved").ToString(),
            ["Directory:Graph:WorkforceExtension"] = partition == "extension" ? "untrusted" : "extension_test",
            ["Directory:Graph:WorkforceValue"] = partition == "value" ? " " : "true",
            ["Directory:Graph:MaximumRecipients"] = partition == "recipients" ? "0" : "5000",
            ["Directory:Graph:MaximumPages"] = partition == "pages" ? "10001" : "1000",
            ["Directory:Graph:MaximumThrottlingRetries"] = partition == "retries" ? "9" : "3",
            ["Directory:Credentials:TenantId"] = Tenant.ToString()
        };
        var services = Services(values);
        services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(builder =>
        {
            builder.PrimaryHandler.Dispose();
            builder.PrimaryHandler = transport;
        }));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IGraphAccessTokenProvider>();
        var directory = scope.ServiceProvider.GetRequiredService<IDirectoryGateway>();
        Assert.IsType<GraphClientCredentialTokenProvider>(tokens);
        Assert.IsType<GraphDirectoryGateway>(directory);
        var error = await Assert.ThrowsAsync<DomainException>(async () =>
        {
            if (partition == "credentials")
                await tokens.GetTokenAsync();
            else
                await directory.GetUserAsync(Guid.NewGuid());
        });
        Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
        Assert.Equal(partition == "credentials"
            ? "Directory credentials are missing or do not match the configured tenant."
            : "Directory workforce policy is not configured or approved. Contact the application operator.", error.Message);
        Assert.Empty(transport.Requests);
    }

    /// <summary>Checks each selected public guard and its exact parameter without partially registering a graph.</summary>
    /// <param name="guard">Public entry point and argument to omit.</param>
    /// <param name="parameter">Exact parameter name carried by ArgumentNullException.</param>
    [Theory]
    [InlineData("core-services", "services")]
    [InlineData("core-configuration", "configuration")]
    [InlineData("core-authentication", "authentication")]
    [InlineData("delivery-services", "services")]
    [InlineData("delivery-configuration", "configuration")]
    [InlineData("events", "services")]
    [InlineData("ui", "services")]
    public void PublicRegistrationGuards_RejectNullWithExactParameter_WithoutMutation(string guard, string parameter)
    {
        var services = new ServiceCollection();
        var configuration = Configuration();
        var error = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = guard switch
            {
                "core-services" => CoreWorkflowRegistration.AddSidequestCoreWorkflows(null!, configuration, Authentication),
                "core-configuration" => services.AddSidequestCoreWorkflows(null!, Authentication),
                "core-authentication" => services.AddSidequestCoreWorkflows(configuration, null!),
                "delivery-services" => DeliveryRegistration.AddSidequestDelivery(null!, configuration),
                "delivery-configuration" => services.AddSidequestDelivery(null!),
                "events" => EventFeatureRegistration.AddEventWorkflows(null!),
                "ui" => EventUiRegistration.AddEventUiRevalidation(null!),
                _ => throw new InvalidOperationException("Unknown guard.")
            };
        });
        Assert.Equal(parameter, error.ParamName);
        Assert.Empty(services);
    }

    internal static IConfiguration Configuration(Dictionary<string, string?>? values = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sidequest"] = "Server=sql.example.invalid;Database=Composition;Integrated Security=true;TrustServerCertificate=true"
        };
        foreach (var pair in values ?? [])
            settings[pair.Key] = pair.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    internal static IServiceCollection Services(Dictionary<string, string?>? values = null, TimeProvider? clock = null)
    {
        var configuration = Configuration(values);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Authentication);
        services.AddScoped<AuthenticationStateProvider, WorkforceAuthenticationStateProvider>();
        services.AddScoped<ICurrentUser, CircuitCurrentUser>();
        if (clock is not null)
            services.AddSingleton(clock);
        services.AddSidequestApplication();
        services.AddSidequestInfrastructure(configuration);
        services.AddSidequestDelivery(configuration);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        services.AddSidequestCoreWorkflows(configuration, Authentication);
        return services;
    }
}
