using System.Collections;
using System.Data;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Authentication;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Verifies persistence-boundary conflict retries, context disposal, and non-retryable admission failures.</summary>
/// <remarks>Each test owns its mutable recorders. The recorders expect sequential provisioning attempts and are not shared across threads.</remarks>
public sealed class ProvisioningRetryTests
{
    /// <summary>Verifies a fresh serializable context per conflict retry and a commit only on the final successful attempt.</summary>
    /// <param name="failedAttempts">The number of persistence conflicts before success, within the two-retry limit.</param>
    /// <returns>A task completing after provisioning and context/transaction lifecycle assertions.</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PersistenceConflictRetriesWithFreshContext(int failedAttempts)
    {
        var contexts = Enumerable.Range(0, failedAttempts + 1)
            .Select(attempt => new ProvisioningContext(EligibleUser(),
                attempt < failedAttempts ? new DomainException(ErrorCode.Conflict, "Concurrent persistence change.") : null))
            .ToArray();
        var factory = new ContextSequence(contexts);

        await Accounts(factory).ProvisionAsync(DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), default);

        Assert.Equal(failedAttempts + 1, factory.Created.Count);
        Assert.Equal(contexts, factory.Created);
        Assert.All(contexts, context =>
        {
            Assert.Equal(1, context.SaveCalls);
            Assert.Equal(IsolationLevel.Serializable, context.Isolation);
            Assert.True(context.Disposed);
            Assert.True(context.Transaction.Disposed);
        });
        Assert.All(contexts.Take(failedAttempts), context => Assert.Equal(0, context.Transaction.Commits));
        Assert.Equal(1, contexts[^1].Transaction.Commits);
        Assert.Equal("Alice", contexts[^1].User.DisplayName);
        Assert.Equal("alice@sample.invalid", contexts[^1].User.Email);
        Assert.NotNull(contexts[^1].User.LastSignedInUtc);
    }

    /// <summary>Verifies propagation of the final conflict after the initial attempt and two retries.</summary>
    /// <returns>A task completing after the retry bound and disposal assertions.</returns>
    [Fact]
    public async Task PersistenceConflictsStopAfterThreeAttempts()
    {
        var error = new DomainException(ErrorCode.Conflict, "Concurrent persistence change.");
        var contexts = Enumerable.Range(0, 3).Select(_ => new ProvisioningContext(EligibleUser(), error)).ToArray();
        var factory = new ContextSequence(contexts);

        var thrown = await Assert.ThrowsAsync<DomainException>(() =>
            Accounts(factory).ProvisionAsync(DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), default));

        Assert.Same(error, thrown);
        Assert.Equal(3, factory.Created.Count);
        Assert.All(contexts, context =>
        {
            Assert.Equal(1, context.SaveCalls);
            Assert.Equal(0, context.Transaction.Commits);
            Assert.True(context.Disposed);
            Assert.True(context.Transaction.Disposed);
        });
    }

    /// <summary>Verifies that Forbidden and Validation persistence failures propagate unchanged without retries.</summary>
    /// <param name="code">The non-conflict domain error returned by the fake persistence boundary.</param>
    /// <returns>A task completing after the single-attempt and no-commit assertions.</returns>
    [Theory]
    [InlineData(ErrorCode.Forbidden)]
    [InlineData(ErrorCode.Validation)]
    public async Task NonConflictPersistenceErrorsDoNotRetry(ErrorCode code)
    {
        var error = new DomainException(code, "Not retryable.");
        var context = new ProvisioningContext(EligibleUser(), error);
        var factory = new ContextSequence(context);

        var thrown = await Assert.ThrowsAsync<DomainException>(() =>
            Accounts(factory).ProvisionAsync(DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), default));

        Assert.Same(error, thrown);
        Assert.Single(factory.Created);
        Assert.Equal(1, context.SaveCalls);
        Assert.Equal(0, context.Transaction.Commits);
        Assert.True(context.Disposed);
        Assert.True(context.Transaction.Disposed);
    }

    /// <summary>Verifies that a disabled local account is rejected without saving, retrying, or changing eligibility.</summary>
    /// <returns>A task completing after the rejection and unchanged-account assertions.</returns>
    [Fact]
    public async Task IneligibleAccountDoesNotRetryOrSave()
    {
        var user = EligibleUser();
        user.IsEligible = false;
        var context = new ProvisioningContext(user);
        var factory = new ContextSequence(context);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            Accounts(factory).ProvisionAsync(DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), default));

        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Single(factory.Created);
        Assert.Equal(0, context.SaveCalls);
        Assert.Equal(0, context.Transaction.Commits);
        Assert.True(context.Disposed);
        Assert.True(context.Transaction.Disposed);
        Assert.False(user.IsEligible);
        Assert.Equal("Before sign-in", user.DisplayName);
        Assert.Null(user.LastSignedInUtc);
    }

    private static WorkforceAccounts Accounts(ISidequestDbContextFactory factory) => new(factory,
        new FoundationAuthenticationSettings(true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null),
        TimeProvider.System, NullLogger<WorkforceAccounts>.Instance);

    private static UserAccount EligibleUser() => new()
    {
        TenantId = DevelopmentPersonas.TenantId,
        ObjectId = DevelopmentPersonas.All[1].ObjectId,
        DisplayName = "Before sign-in",
        IsEligible = true
    };

    /// <summary>Supplies distinct scripted contexts and checks disposal ordering between provisioning attempts.</summary>
    /// <param name="contexts">The contexts permitted for the expected attempt sequence.</param>
    private sealed class ContextSequence(params ProvisioningContext[] contexts) : ISidequestDbContextFactory
    {
        /// <summary>Gets the contexts supplied to provisioning, in creation order.</summary>
        public List<ProvisioningContext> Created { get; } = [];

        /// <inheritdoc/>
        /// <remarks>Asserts disposal of the prior context and transaction before supplying the next context.</remarks>
        public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
        {
            if (Created.Count > 0)
            {
                Assert.True(Created[^1].Disposed);
                Assert.True(Created[^1].Transaction.Disposed);
            }
            Assert.True(Created.Count < contexts.Length, "Unexpected additional provisioning attempt.");
            var context = contexts[Created.Count];
            Created.Add(context);
            return Task.FromResult<ISidequestDbContext>(context);
        }
    }

    /// <summary>Records account provisioning operations without connecting to SQL.</summary>
    /// <param name="user">The persisted account returned by the user query.</param>
    /// <param name="saveError">The failure returned by each save, or no failure.</param>
    /// <remarks>Only the Users set is supported; all unrelated DbSet properties deliberately throw NotSupportedException.</remarks>
    private sealed class ProvisioningContext(UserAccount user, Exception? saveError = null) : ISidequestDbContext
    {
        /// <summary>Gets the mutable local account queried by this attempt.</summary>
        public UserAccount User { get; } = user;
        /// <inheritdoc/>
        public DbSet<UserAccount> Users { get; } = new UserSet(user);
        /// <summary>Gets the transaction recorder returned by this context.</summary>
        public TrackingTransaction Transaction { get; } = new();
        /// <summary>Gets the number of save attempts, including failed saves.</summary>
        public int SaveCalls { get; private set; }
        /// <summary>Gets whether asynchronous context disposal has occurred.</summary>
        public bool Disposed { get; private set; }
        /// <summary>Gets the transaction isolation level requested by provisioning.</summary>
        public IsolationLevel Isolation { get; private set; }

        /// <inheritdoc/>
        public Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Serializable,
            CancellationToken cancellationToken = default)
        {
            Isolation = isolationLevel;
            return Task.FromResult<IDbContextTransaction>(Transaction);
        }

        /// <inheritdoc/>
        /// <remarks>Records a save attempt and returns the configured failure or a successful affected-row count.</remarks>
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return saveError is null ? Task.FromResult(1) : Task.FromException<int>(saveError);
        }

        /// <inheritdoc/>
        public Task LockEventAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not acquire Event locks.");

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc/>
        public DbSet<Administrator> Administrators => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<Event> Events => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<EventOwner> EventOwners => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<EventMembership> EventMemberships => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<EventMembershipRequest> MembershipRequests => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<EventInvitation> EventInvitations => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<Quest> Quests => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<QuestOwner> QuestOwners => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<QuestInvitation> QuestInvitations => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<QuestParticipation> Participations => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<AuditEntry> AuditEntries => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<EventStatusHistory> EventStatusHistory => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<QuestStatusHistory> QuestStatusHistory => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<Notification> Notifications => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<NotificationPreference> NotificationPreferences => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<EventNotificationPreference> EventNotificationPreferences => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<NotificationTemplate> NotificationTemplates => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<ApplicationSetting> ApplicationSettings => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<OutboxMessage> OutboxMessages => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<ScheduledWork> ScheduledWork => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<NotificationDelivery> NotificationDeliveries => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<CalendarDeliveryState> CalendarDeliveryStates => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<MediaAsset> MediaAssets => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<BulkMembershipOperation> BulkOperations => throw new NotSupportedException();
        /// <inheritdoc/>
        public DbSet<BulkMembershipRecipient> BulkRecipients => throw new NotSupportedException();
    }

    /// <summary>Adapts a single account to the asynchronous scalar query used by provisioning.</summary>
    /// <param name="user">The account exposed through LINQ-to-Objects query evaluation.</param>
    private sealed class UserSet(UserAccount user) : DbSet<UserAccount>, IQueryable<UserAccount>
    {
        private readonly IQueryable<UserAccount> query = new[] { user }.AsQueryable();
        /// <inheritdoc/>
        /// <exception cref="NotSupportedException">This LINQ-only fixture does not supply EF model metadata.</exception>
        public override IEntityType EntityType => throw new NotSupportedException();
        /// <inheritdoc/>
        Type IQueryable.ElementType => query.ElementType;
        /// <inheritdoc/>
        Expression IQueryable.Expression => query.Expression;
        /// <inheritdoc/>
        IQueryProvider IQueryable.Provider => new SingleUserQueryProvider(query.Provider);
        /// <inheritdoc/>
        IEnumerator<UserAccount> IEnumerable<UserAccount>.GetEnumerator() => query.GetEnumerator();
        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => query.GetEnumerator();
    }

    /// <summary>Wraps the fixture's single-user scalar lookup in an asynchronous EF query result.</summary>
    /// <param name="inner">The LINQ-to-Objects provider evaluating the account predicate.</param>
    private sealed class SingleUserQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        /// <inheritdoc/>
        public IQueryable CreateQuery(Expression expression) => inner.CreateQuery(expression);
        /// <inheritdoc/>
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => inner.CreateQuery<TElement>(expression);
        /// <inheritdoc/>
        public object? Execute(Expression expression) => inner.Execute(expression);
        /// <inheritdoc/>
        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
        /// <inheritdoc/>
        /// <remarks>Supports the single-user scalar result shape required by these provisioning tests.</remarks>
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default) =>
            (TResult)(object)Task.FromResult(inner.Execute<UserAccount?>(expression));
    }

    /// <summary>Records commit and disposal requests without executing database operations.</summary>
    private sealed class TrackingTransaction : IDbContextTransaction
    {
        /// <inheritdoc/>
        public Guid TransactionId { get; } = Guid.NewGuid();
        /// <summary>Gets the number of commit requests made by provisioning.</summary>
        public int Commits { get; private set; }
        /// <summary>Gets whether synchronous or asynchronous disposal was requested.</summary>
        public bool Disposed { get; private set; }
        /// <inheritdoc/>
        public void Commit() => Commits++;
        /// <inheritdoc/>
        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commit();
            return Task.CompletedTask;
        }
        /// <inheritdoc/>
        public void Rollback()
        {
        }
        /// <inheritdoc/>
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        /// <inheritdoc/>
        public void Dispose() => Disposed = true;
        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
