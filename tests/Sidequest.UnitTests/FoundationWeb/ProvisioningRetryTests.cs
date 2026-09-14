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

public sealed class ProvisioningRetryTests
{
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

    private sealed class ContextSequence(params ProvisioningContext[] contexts) : ISidequestDbContextFactory
    {
        public List<ProvisioningContext> Created { get; } = [];

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

    private sealed class ProvisioningContext(UserAccount user, Exception? saveError = null) : ISidequestDbContext
    {
        public UserAccount User { get; } = user;
        public DbSet<UserAccount> Users { get; } = new UserSet(user);
        public TrackingTransaction Transaction { get; } = new();
        public int SaveCalls { get; private set; }
        public bool Disposed { get; private set; }
        public IsolationLevel Isolation { get; private set; }

        public Task<IDbContextTransaction> BeginTransactionAsync(IsolationLevel isolationLevel = IsolationLevel.Serializable,
            CancellationToken cancellationToken = default)
        {
            Isolation = isolationLevel;
            return Task.FromResult<IDbContextTransaction>(Transaction);
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return saveError is null ? Task.FromResult(1) : Task.FromException<int>(saveError);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public DbSet<Administrator> Administrators => throw new NotSupportedException();
        public DbSet<Event> Events => throw new NotSupportedException();
        public DbSet<EventOwner> EventOwners => throw new NotSupportedException();
        public DbSet<EventMembership> EventMemberships => throw new NotSupportedException();
        public DbSet<EventMembershipRequest> MembershipRequests => throw new NotSupportedException();
        public DbSet<EventInvitation> EventInvitations => throw new NotSupportedException();
        public DbSet<Quest> Quests => throw new NotSupportedException();
        public DbSet<QuestOwner> QuestOwners => throw new NotSupportedException();
        public DbSet<QuestInvitation> QuestInvitations => throw new NotSupportedException();
        public DbSet<QuestParticipation> Participations => throw new NotSupportedException();
        public DbSet<AuditEntry> AuditEntries => throw new NotSupportedException();
        public DbSet<EventStatusHistory> EventStatusHistory => throw new NotSupportedException();
        public DbSet<QuestStatusHistory> QuestStatusHistory => throw new NotSupportedException();
        public DbSet<Notification> Notifications => throw new NotSupportedException();
        public DbSet<NotificationPreference> NotificationPreferences => throw new NotSupportedException();
        public DbSet<EventNotificationPreference> EventNotificationPreferences => throw new NotSupportedException();
        public DbSet<NotificationTemplate> NotificationTemplates => throw new NotSupportedException();
        public DbSet<ApplicationSetting> ApplicationSettings => throw new NotSupportedException();
        public DbSet<OutboxMessage> OutboxMessages => throw new NotSupportedException();
        public DbSet<ScheduledWork> ScheduledWork => throw new NotSupportedException();
        public DbSet<NotificationDelivery> NotificationDeliveries => throw new NotSupportedException();
        public DbSet<CalendarDeliveryState> CalendarDeliveryStates => throw new NotSupportedException();
        public DbSet<MediaAsset> MediaAssets => throw new NotSupportedException();
        public DbSet<BulkMembershipOperation> BulkOperations => throw new NotSupportedException();
        public DbSet<BulkMembershipRecipient> BulkRecipients => throw new NotSupportedException();
    }

    private sealed class UserSet(UserAccount user) : DbSet<UserAccount>, IQueryable<UserAccount>
    {
        private readonly IQueryable<UserAccount> query = new[] { user }.AsQueryable();
        public override IEntityType EntityType => throw new NotSupportedException();
        Type IQueryable.ElementType => query.ElementType;
        Expression IQueryable.Expression => query.Expression;
        IQueryProvider IQueryable.Provider => new SingleUserQueryProvider(query.Provider);
        IEnumerator<UserAccount> IEnumerable<UserAccount>.GetEnumerator() => query.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => query.GetEnumerator();
    }

    private sealed class SingleUserQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => inner.CreateQuery(expression);
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => inner.CreateQuery<TElement>(expression);
        public object? Execute(Expression expression) => inner.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default) =>
            (TResult)(object)Task.FromResult(inner.Execute<UserAccount?>(expression));
    }

    private sealed class TrackingTransaction : IDbContextTransaction
    {
        public Guid TransactionId { get; } = Guid.NewGuid();
        public int Commits { get; private set; }
        public bool Disposed { get; private set; }
        public void Commit() => Commits++;
        public Task CommitAsync(CancellationToken cancellationToken = default) { Commit(); return Task.CompletedTask; }
        public void Rollback() { }
        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
