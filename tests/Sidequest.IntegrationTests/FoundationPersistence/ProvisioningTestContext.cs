using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.Web.Authentication;

namespace Sidequest.IntegrationTests.FoundationPersistence;

internal sealed class ProvisioningTestContext(SqlTestDatabase database) : ISidequestDbContextFactory
{
    private readonly ProvisioningLog log = new();

    internal Guid TenantId { get; } = Guid.NewGuid();
    internal Guid BootstrapObjectId { get; } = Guid.NewGuid();
    internal IReadOnlyCollection<string> Warnings => log.Warnings.ToArray();

    internal ClaimsPrincipal Principal(Guid objectId, string name = "First sign-in", string email = "first@sample.invalid") =>
        new(new ClaimsIdentity(
        [
            new("tid", TenantId.ToString()), new("oid", objectId.ToString()), new("roles", "Workforce"),
            new("name", name), new("preferred_username", email)
        ], "TestValidated"));

    internal WorkforceAccounts Accounts(IInterceptor? observer = null, DateTimeOffset? now = null) =>
        new(observer is null ? this : new ObservedContextFactory(database, observer),
            new(false, TenantId, "Workforce", BootstrapObjectId),
            new FixedClock(now ?? FoundationSeed.Now), log);

    /// <inheritdoc />
    public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ISidequestDbContext>(database.CreateContext());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ProvisioningLog : ILogger<WorkforceAccounts>
    {
        internal ConcurrentQueue<string> Warnings { get; } = new();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Enqueue(formatter(state, exception));
        }
    }
}
