using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Media;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Application.Security;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Delivery;
using Sidequest.Infrastructure.Directory;
using Sidequest.Infrastructure.Media;
using Sidequest.Infrastructure.Persistence;
using Sidequest.UnitTests.CoreComposition;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.ReleaseOperations;

/// <summary>Verifies bounded telemetry dimensions and transparent execution without connecting to providers or SQL.</summary>
[Collection("Release operational metrics")]
public sealed class OperationalActivityMetricsTests
{
    /// <summary>Each fixed activity emits one completion and exact monotonic elapsed seconds, preserving the same result object.</summary>
    /// <returns>Completion after all fixed activity categories have executed once.</returns>
    [Fact]
    public async Task FixedActivitiesPreserveResultsAndMeasureElapsedTimeExactlyOnce()
    {
        using var capture = new ActivityCapture();
        var clock = new OperationalTestClock();
        using var metrics = new OperationalActivityMetrics(clock);
        var result = new object();
        var expected = new[] { "directory_user_search", "directory_group_search", "directory_user_lookup",
            "directory_group_expansion", "image_sanitization", "email_submission", "authorize_user",
            "authorize_administrator", "authorize_event", "authorize_quest" };
        var calls = 0;
        foreach (var activity in Enum.GetValues<OperationalActivity>())
        {
            Assert.Same(result, await metrics.ObserveAsync(activity, () =>
            {
                calls++;
                clock.Advance(TimeSpan.FromMilliseconds(125));
                return Task.FromResult(result);
            }, default));
        }
        Assert.Equal(expected.Length, calls);
        var readings = capture.Read();
        Assert.Equal(expected.Length * 2, readings.Length);
        foreach (var operation in expected)
            AssertObservation(readings, operation, "succeeded", .125);
    }

    /// <summary>Known failures and caller cancellation retain their exact exception and never emit success, raw messages or retry the operation.</summary>
    /// <param name="kind">The classified failure partition.</param>
    /// <param name="outcome">The only permitted metric outcome for this partition.</param>
    /// <returns>Completion after the unchanged exception and two aggregate measurements are inspected.</returns>
    [Theory]
    [InlineData("validation", "rejected")]
    [InlineData("forbidden", "denied")]
    [InlineData("missing", "unavailable")]
    [InlineData("conflict", "conflict")]
    [InlineData("dependency", "dependency_failure")]
    [InlineData("permanent", "permanent_failure")]
    [InlineData("retryable", "retryable_failure")]
    [InlineData("uncertain", "uncertain_failure")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("timeout", "failed")]
    [InlineData("unexpected", "failed")]
    public async Task FailuresRemainUnchangedAndAreClassifiedWithoutPrivateData(string kind, string outcome)
    {
        using var capture = new ActivityCapture();
        var clock = new OperationalTestClock();
        using var metrics = new OperationalActivityMetrics(clock);
        using var cancellation = new CancellationTokenSource();
        const string secret = "private-user-address-token-provider-body";
        var failure = kind switch
        {
            "validation" => new DomainException(ErrorCode.Validation, secret),
            "forbidden" => new DomainException(ErrorCode.Forbidden, secret),
            "missing" => new DomainException(ErrorCode.NotFound, secret),
            "conflict" => new DomainException(ErrorCode.Conflict, secret),
            "dependency" => new DomainException(ErrorCode.DependencyUnavailable, secret),
            "permanent" => new DeliveryTransportException(TransportOutcome.Permanent, secret),
            "retryable" => new DeliveryTransportException(TransportOutcome.Retryable, secret, TimeSpan.FromSeconds(30)),
            "uncertain" => new DeliveryTransportException(TransportOutcome.Uncertain, secret),
            "cancelled" or "timeout" => new OperationCanceledException(secret, cancellation.Token),
            _ => (Exception)new InvalidOperationException(secret)
        };
        if (kind == "cancelled")
            cancellation.Cancel();
        var calls = 0;
        var actual = await Record.ExceptionAsync(() => metrics.ObserveAsync<object>(OperationalActivity.EmailSubmission, () =>
        {
            calls++;
            clock.Advance(TimeSpan.FromMilliseconds(250));
            throw failure;
        }, cancellation.Token));
        Assert.Same(failure, actual);
        Assert.Equal(1, calls);
        var readings = capture.Read();
        Assert.Equal(2, readings.Length);
        AssertObservation(readings, "email_submission", outcome, .25);
        Assert.All(readings, reading => Assert.DoesNotContain(secret, string.Join(",", reading.Tags.Values)));
    }

    /// <summary>Invalid activities fail before invocation, and disposal does not cancel or change already-running application work.</summary>
    /// <returns>Completion after the pending operation finishes with measurement collection detached.</returns>
    [Fact]
    public async Task InvalidActivityDoesNotInvokeAndDisposalDoesNotCancelWork()
    {
        using var capture = new ActivityCapture();
        using var metrics = new OperationalActivityMetrics(new OperationalTestClock());
        var called = false;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => metrics.ObserveAsync((OperationalActivity)999, () =>
        {
            called = true;
            return Task.FromResult(1);
        }, default));
        Assert.False(called);
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = metrics.ObserveAsync(OperationalActivity.ImageSanitization, () => completion.Task, default);
        Assert.False(pending.IsCompleted);
        Assert.Empty(capture.Read());
        metrics.Dispose();
        var result = new object();
        completion.SetResult(result);
        Assert.Same(result, await pending);
        Assert.Empty(capture.Read());
    }

    /// <summary>HTTP outcomes are counted after downstream completion with no path, identity, body or exception dimensions.</summary>
    /// <param name="status">The final downstream response status.</param>
    /// <param name="outcome">The fixed expected HTTP outcome.</param>
    /// <returns>Completion after the middleware records the unchanged response.</returns>
    [Theory]
    [InlineData(101, "other")]
    [InlineData(204, "succeeded")]
    [InlineData(302, "redirect")]
    [InlineData(400, "client_error")]
    [InlineData(401, "unauthenticated")]
    [InlineData(403, "forbidden")]
    [InlineData(404, "client_error")]
    [InlineData(500, "server_error")]
    [InlineData(503, "server_error")]
    public async Task HttpRecordsOnlyFinalBoundedOutcome(int status, string outcome)
    {
        using var capture = new ActivityCapture();
        using var metrics = new OperationalActivityMetrics(new OperationalTestClock());
        var context = new DefaultHttpContext();
        context.Request.Path = "/private-user-and-resource";
        var calls = 0;
        var middleware = new OperationalHttpMetricsMiddleware(http =>
        {
            Assert.Same(context, http);
            calls++;
            http.Response.StatusCode = status;
            return Task.CompletedTask;
        }, metrics);
        await middleware.InvokeAsync(context);
        Assert.Equal(1, calls);
        Assert.Equal(status, context.Response.StatusCode);
        var reading = Assert.Single(capture.Read());
        Assert.Equal("sidequest.http.completed", reading.Name);
        Assert.Equal(1, reading.Value);
        var tag = Assert.Single(reading.Tags);
        Assert.Equal("outcome", tag.Key);
        Assert.Equal(outcome, tag.Value);
    }

    /// <summary>Uncaught HTTP failures never become successful 200 measurements and are not swallowed, including client aborts.</summary>
    /// <param name="aborted">Whether the client request token was cancelled.</param>
    /// <returns>Completion after the same failure has propagated and its aggregate outcome is inspected.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpPreservesUncaughtFailureAndDistinguishesAbort(bool aborted)
    {
        using var capture = new ActivityCapture();
        using var metrics = new OperationalActivityMetrics(new OperationalTestClock());
        using var cancellation = new CancellationTokenSource();
        if (aborted) cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var failure = new InvalidOperationException("private-do-not-export");
        var middleware = new OperationalHttpMetricsMiddleware(_ => throw failure, metrics);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context)));
        Assert.Equal(aborted ? "aborted" : "server_error", Assert.Single(capture.Read()).Tags["outcome"]);
    }

    /// <summary>The real middleware activation path accepts disabled monitoring without a metrics dependency and emits only when opted in.</summary>
    /// <param name="enabled">Whether this host opts in to aggregate monitoring.</param>
    /// <returns>Completion after an in-memory ASP.NET pipeline serves an unchanged forbidden response.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredMiddlewarePipelineKeepsDisabledHostsInert(bool enabled)
    {
        using var capture = new ActivityCapture();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:Monitoring:Enabled"] = enabled.ToString()
        }).Build();
        var services = new ServiceCollection().AddLogging().AddSidequestOperationalMonitoring(configuration);
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseMiddleware<OperationalHttpMetricsMiddleware>();
        app.Run(context =>
        {
            context.Response.StatusCode = 403;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = provider };
        await app.Build()(context);
        Assert.Equal(403, context.Response.StatusCode);
        if (enabled)
            Assert.Equal("forbidden", Assert.Single(capture.Read()).Tags["outcome"]);
        else
            Assert.Empty(capture.Read());
    }

    /// <summary>Monitoring decorates real host ports only when enabled, preserves lifetimes, and never replaces a custom authorization policy.</summary>
    /// <param name="enabled">Whether operational monitoring is explicitly enabled.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompositionPreservesOptInLifetimesAndCustomAuthorization(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:Monitoring:Enabled"] = enabled.ToString()
        }).Build();
        var services = CoreWorkflowRegistrationTests.Services();
        services.AddSidequestMedia(configuration);
        services.AddSidequestOperationalMonitoring(configuration);
        using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.IsType(enabled ? typeof(ObservedDirectoryGateway) : typeof(GraphDirectoryGateway),
            first.ServiceProvider.GetRequiredService<IDirectoryGateway>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IDirectoryGateway>(), first.ServiceProvider.GetRequiredService<IDirectoryGateway>());
        Assert.IsType(enabled ? typeof(ObservedEmailGateway) : typeof(AcsEmailGateway), provider.GetRequiredService<IEmailGateway>());
        Assert.Same(first.ServiceProvider.GetRequiredService<IEmailGateway>(), second.ServiceProvider.GetRequiredService<IEmailGateway>());
        Assert.IsType(enabled ? typeof(ObservedImageSanitizer) : typeof(SkiaImageSanitizer), provider.GetRequiredService<IImageSanitizer>());
        Assert.Same(first.ServiceProvider.GetRequiredService<IImageSanitizer>(), second.ServiceProvider.GetRequiredService<IImageSanitizer>());
        Assert.IsType(enabled ? typeof(ObservedResourceAccess) : typeof(ResourceAccess), first.ServiceProvider.GetRequiredService<IResourceAccess>());
        Assert.Same(first.ServiceProvider.GetRequiredService<IResourceAccess>(), first.ServiceProvider.GetRequiredService<IResourceAccess>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IResourceAccess>(), second.ServiceProvider.GetRequiredService<IResourceAccess>());
        Assert.Equal(enabled, provider.GetService<OperationalActivityMetrics>() is not null);

        IServiceCollection custom = new ServiceCollection();
        var descriptor = ServiceDescriptor.Scoped<IResourceAccess>(_ => throw new InvalidOperationException("custom policy"));
        custom.Add(descriptor);
        custom.AddSidequestOperationalMonitoring(configuration);
        Assert.Same(descriptor, Assert.Single(custom, entry => entry.ServiceType == typeof(IResourceAccess)));
    }

    /// <summary>Every decorator forwards exact caller-owned objects, identities, flags and cancellation tokens once without touching SQL or consuming streams.</summary>
    /// <param name="ownerOnly">The unchanged Event/Quest ownership requirement.</param>
    /// <param name="moderation">The unchanged Quest moderation requirement.</param>
    /// <returns>Completion after all ten port calls return their original result references.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DecoratorsPreserveAllArgumentsResultsAndCancellation(bool ownerOnly, bool moderation)
    {
        using var capture = new ActivityCapture();
        using var metrics = new OperationalActivityMetrics(new OperationalTestClock());
        var inner = new RecordingPorts();
        var directory = new ObservedDirectoryGateway(inner, metrics);
        var email = new ObservedEmailGateway(inner, metrics);
        var images = new ObservedImageSanitizer(inner, metrics);
        var access = new ObservedResourceAccess(inner, metrics);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        const string query = "private-search-never-export";
        using var stream = new MemoryStream([1, 2, 3]);
        await using var db = new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>().Options);
        var message = new EmailMessage("private@sample.invalid", "private", "private", "private", "private-key");

        Assert.Same(inner.Users, await directory.SearchUsersAsync(query, token));
        Assert.Same(inner.Groups, await directory.SearchGroupsAsync(query, token));
        Assert.Same(inner.DirectoryUser, await directory.GetUserAsync(userId, token));
        Assert.Same(inner.Users, await directory.ExpandGroupAsync(resourceId, token));
        Assert.Same(inner.Receipt, await email.SendAsync(message, token));
        Assert.Same(inner.Image, await images.SanitizeAsync(stream, token));
        Assert.Same(inner.User, await access.RequireUserAsync(db, token));
        Assert.Same(inner.User, await access.RequireAdministratorAsync(db, token));
        Assert.Same(inner.Event, await access.RequireEventAsync(db, resourceId, userId, ownerOnly, token));
        Assert.Same(inner.Quest, await access.RequireQuestAsync(db, resourceId, userId, ownerOnly, moderation, token));
        object?[][] expected =
        [
            [query, token], [query, token], [userId, token], [resourceId, token], [message, token],
            [stream, token], [db, token], [db, token], [db, resourceId, userId, ownerOnly, token],
            [db, resourceId, userId, ownerOnly, moderation, token]
        ];
        Assert.Equal(expected.Length, inner.Calls.Count);
        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(expected[index], inner.Calls[index]);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
        var readings = capture.Read();
        Assert.Equal(20, readings.Length);
        Assert.Equal(10, readings.Where(reading => reading.Name == "sidequest.operation.completed")
            .Select(reading => reading.Tags["operation"]).Distinct().Count());
        Assert.All(readings, reading =>
        {
            Assert.Equal(2, reading.Tags.Count);
            Assert.Equal("succeeded", reading.Tags["outcome"]);
            Assert.DoesNotContain(query, string.Join(",", reading.Tags.Values));
            Assert.DoesNotContain(userId.ToString(), string.Join(",", reading.Tags.Values));
        });
    }

    private static void AssertObservation(Reading[] readings, string operation, string outcome, double seconds)
    {
        var selected = readings.Where(reading => reading.Tags.GetValueOrDefault("operation") as string == operation).ToArray();
        Assert.Equal(2, selected.Length);
        Assert.All(selected, reading =>
        {
            Assert.Equal(2, reading.Tags.Count);
            Assert.Equal(outcome, reading.Tags["outcome"]);
        });
        Assert.Equal(1, Assert.Single(selected, reading => reading.Name == "sidequest.operation.completed").Value);
        Assert.Equal(seconds, Assert.Single(selected, reading => reading.Name == "sidequest.operation.duration").Value);
    }

    private sealed record Reading(string Name, double Value, Dictionary<string, object?> Tags);

    private sealed class RecordingPorts : IDirectoryGateway, IEmailGateway, IImageSanitizer, IResourceAccess
    {
        internal RecordingPorts()
        {
            Users = [DirectoryUser];
            Groups = [new(Guid.NewGuid(), "Private group")];
        }

        internal DirectoryUser DirectoryUser { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "Private", "private@sample.invalid", true);
        internal IReadOnlyList<DirectoryUser> Users { get; }
        internal IReadOnlyList<DirectoryGroup> Groups { get; }
        internal EmailReceipt Receipt { get; } = new("private-receipt");
        internal SanitizedImage Image { get; } = new([1, 2, 3], "image/png", 1, 1);
        internal UserAccount User { get; } = new();
        internal Event Event { get; } = new();
        internal Quest Quest { get; } = new();
        internal List<object?[]> Calls { get; } = [];

        private Task<T> Return<T>(T result, params object?[] arguments)
        {
            Calls.Add(arguments);
            return Task.FromResult(result);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default) =>
            Return(Users, query, cancellationToken);
        /// <inheritdoc />
        public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default) =>
            Return(Groups, query, cancellationToken);
        /// <inheritdoc />
        public Task<DirectoryUser> GetUserAsync(Guid objectId, CancellationToken cancellationToken = default) =>
            Return(DirectoryUser, objectId, cancellationToken);
        /// <inheritdoc />
        public Task<IReadOnlyList<DirectoryUser>> ExpandGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
            Return(Users, groupId, cancellationToken);
        /// <inheritdoc />
        public Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Return(Receipt, message, cancellationToken);
        /// <inheritdoc />
        public Task<SanitizedImage> SanitizeAsync(Stream content, CancellationToken cancellationToken = default) =>
            Return(Image, content, cancellationToken);
        /// <inheritdoc />
        public Task<UserAccount> RequireUserAsync(ISidequestDbContext db, CancellationToken cancellationToken = default) =>
            Return(User, db, cancellationToken);
        /// <inheritdoc />
        public Task<UserAccount> RequireAdministratorAsync(ISidequestDbContext db, CancellationToken cancellationToken = default) =>
            Return(User, db, cancellationToken);
        /// <inheritdoc />
        public Task<Event> RequireEventAsync(ISidequestDbContext db, Guid eventId, Guid userId, bool ownerOnly = false,
            CancellationToken cancellationToken = default) => Return(Event, db, eventId, userId, ownerOnly, cancellationToken);
        /// <inheritdoc />
        public Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid questId, Guid userId, bool ownerOnly = false,
            bool moderation = false, CancellationToken cancellationToken = default) =>
            Return(Quest, db, questId, userId, ownerOnly, moderation, cancellationToken);
    }

    private sealed class ActivityCapture : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly List<Reading> readings = [];

        internal ActivityCapture()
        {
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == OperationalQueueMetrics.MeterName && instrument.Meter.Version == "1.0.0" &&
                    !instrument.Name.StartsWith("sidequest.queue.", StringComparison.Ordinal))
                    owner.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.Start();
        }

        internal Reading[] Read()
        {
            lock (readings) return readings.ToArray();
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (readings) readings.Add(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        }

        /// <inheritdoc />
        public void Dispose() => listener.Dispose();
    }
}
