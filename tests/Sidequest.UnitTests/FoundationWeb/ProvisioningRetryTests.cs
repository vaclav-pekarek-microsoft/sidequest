using System.Collections;
using System.Data;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Persistence;
using Sidequest.Web.Authentication;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Verifies persistence-boundary conflict retries, context disposal, and non-retryable admission failures.</summary>
/// <remarks>Each test owns its mutable recorders. The recorders expect sequential provisioning attempts and are not shared across threads.</remarks>
public sealed class ProvisioningRetryTests
{
    /// <summary>Overlapping sign-ins preserve the committed account identity when a losing first insert or existing-account update retries a normalized or EF-wrapped conflict.</summary>
    /// <param name="firstSignIn">Whether both initial lookups precede first provisioning rather than read an existing account.</param>
    /// <param name="wrapped">Whether EF preserves the normalized conflict inside its update exception.</param>
    /// <returns>Completion after a controlled winning commit, fresh losing retry, exact identity/contact checks and disposal of all contexts.</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ConcurrentSignInsRetryConflictAndKeepCommittedIdentity(bool firstSignIn, bool wrapped)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var winnerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loserEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerUser = EligibleUser();
        var loserUser = EligibleUser();
        loserUser.Id = winnerUser.Id;
        var winner = new ProvisioningContext(firstSignIn ? null : winnerUser, beforeSave: token =>
        {
            winnerEntered.TrySetResult();
            return releaseWinner.Task.WaitAsync(token);
        });
        var loser = new ProvisioningContext(firstSignIn ? null : loserUser, Conflict(wrapped), token =>
        {
            loserEntered.TrySetResult();
            return releaseLoser.Task.WaitAsync(token);
        });
        var reloaded = EligibleUser();
        var retry = new ProvisioningContext(reloaded);
        var winnerFactory = new ContextSequence(winner);
        var loserFactory = new ContextSequence(loser, retry);
        var winningSignIn = Accounts(winnerFactory).ProvisionAsync(
            DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), deadline.Token);
        var losingSignIn = Accounts(loserFactory).ProvisionAsync(
            DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), deadline.Token);
        try
        {
            await Task.WhenAll(winnerEntered.Task, loserEntered.Task).WaitAsync(deadline.Token);
            Assert.Equal(0, winner.Transaction.Commits);
            Assert.Equal(0, loser.Transaction.Commits);
            releaseWinner.TrySetResult();
            await winningSignIn;
            reloaded.Id = winner.User.Id;
            releaseLoser.TrySetResult();
            await losingSignIn;
        }
        finally
        {
            releaseWinner.TrySetResult();
            releaseLoser.TrySetResult();
            await Task.WhenAll(winningSignIn, losingSignIn);
        }

        Assert.Single(winnerFactory.Created);
        Assert.Equal(2, loserFactory.Created.Count);
        Assert.Equal(1, winner.Transaction.Commits);
        Assert.Equal(0, loser.Transaction.Commits);
        Assert.Equal(1, retry.Transaction.Commits);
        Assert.Equal(winner.User.Id, retry.User.Id);
        Assert.Equal(firstSignIn ? 1 : 0, winner.AddedUsers);
        Assert.Equal(firstSignIn ? 1 : 0, loser.AddedUsers);
        Assert.Equal(0, retry.AddedUsers);
        if (firstSignIn) Assert.NotEqual(loser.User.Id, retry.User.Id);
        Assert.All(new[] { winner, loser, retry }, context =>
        {
            Assert.Equal(DevelopmentPersonas.TenantId, context.User.TenantId);
            Assert.Equal(DevelopmentPersonas.All[1].ObjectId, context.User.ObjectId);
            Assert.Equal("Alice", context.User.DisplayName);
            Assert.Equal("alice@sample.invalid", context.User.Email);
            Assert.True(context.User.IsEligible);
            Assert.NotNull(context.User.LastSignedInUtc);
            Assert.Equal(IsolationLevel.Serializable, context.Isolation);
            Assert.Equal(1, context.SaveCalls);
            Assert.True(context.Disposed);
            Assert.True(context.Transaction.Disposed);
        });
    }

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
    /// <param name="wrapped">Whether the persistence boundary preserves EF's update wrapper.</param>
    /// <returns>A task completing after the retry bound, original exception and disposal assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceConflictsStopAfterThreeAttempts(bool wrapped)
    {
        var error = Conflict(wrapped);
        var contexts = Enumerable.Range(0, 3).Select(_ => new ProvisioningContext(EligibleUser(), error)).ToArray();
        var factory = new ContextSequence(contexts);

        var thrown = await Record.ExceptionAsync(() =>
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
    /// <param name="wrapped">Whether EF wraps the non-retryable domain failure.</param>
    /// <returns>A task completing after the single-attempt and no-commit assertions.</returns>
    [Theory]
    [InlineData(ErrorCode.Forbidden, false)]
    [InlineData(ErrorCode.Forbidden, true)]
    [InlineData(ErrorCode.Validation, false)]
    [InlineData(ErrorCode.Validation, true)]
    public async Task NonConflictPersistenceErrorsDoNotRetry(ErrorCode code, bool wrapped)
    {
        Exception error = new DomainException(code, "Not retryable.");
        if (wrapped) error = new DbUpdateException("Persistence failed.", error);
        var context = new ProvisioningContext(EligibleUser(), error);
        var factory = new ContextSequence(context);

        var thrown = await Record.ExceptionAsync(() =>
            Accounts(factory).ProvisionAsync(DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), default));

        Assert.Same(error, thrown);
        Assert.Single(factory.Created);
        Assert.Equal(1, context.SaveCalls);
        Assert.Equal(0, context.Transaction.Commits);
        Assert.True(context.Disposed);
        Assert.True(context.Transaction.Disposed);
    }

    /// <summary>A fresh retry observes a concurrent disable or verified departure and cannot authenticate using the earlier eligible account.</summary>
    /// <param name="departed">Whether the winning change records departure rather than disables eligibility.</param>
    /// <param name="wrapped">Whether the initial conflict retains its EF wrapper.</param>
    /// <returns>Completion after current-account denial, no retry save/commit and unchanged reloaded contact fields.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConflictRetryRechecksCurrentEligibility(bool departed, bool wrapped)
    {
        var original = EligibleUser();
        var reloaded = EligibleUser();
        reloaded.Id = original.Id;
        if (departed) reloaded.DepartureVerifiedUtc = DateTimeOffset.UnixEpoch;
        else reloaded.IsEligible = false;
        var first = new ProvisioningContext(original, Conflict(wrapped));
        var retry = new ProvisioningContext(reloaded);
        var factory = new ContextSequence(first, retry);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            Accounts(factory).ProvisionAsync(DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), default));

        Assert.Equal(ErrorCode.Forbidden, error.Code);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(1, first.SaveCalls);
        Assert.Equal(0, retry.SaveCalls);
        Assert.Equal("Before sign-in", reloaded.DisplayName);
        Assert.Null(reloaded.LastSignedInUtc);
        Assert.All(factory.Created, context =>
        {
            Assert.Equal(0, context.Transaction.Commits);
            Assert.True(context.Disposed);
            Assert.True(context.Transaction.Disposed);
        });
    }

    /// <summary>Cancellation and unrelated EF failures preserve the original exception without provisioning retries.</summary>
    /// <param name="cancelled">Whether the EF wrapper contains cancellation rather than an unrelated persistence failure.</param>
    /// <returns>Completion after exact exception identity, a single attempt and no committed changes.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonConflictEfFailuresDoNotRetry(bool cancelled)
    {
        Exception cause = cancelled ? new OperationCanceledException() : new InvalidOperationException("Unrelated failure.");
        var error = new DbUpdateException("Persistence failed.", cause);
        var context = new ProvisioningContext(EligibleUser(), error);
        var factory = new ContextSequence(context);

        Assert.Same(error, await Record.ExceptionAsync(() =>
            Accounts(factory).ProvisionAsync(DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All[1]), default)));

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

    private static Exception Conflict(bool wrapped)
    {
        var error = new DomainException(ErrorCode.Conflict, "Concurrent persistence change.");
        return wrapped ? new DbUpdateException("Persistence failed.", error) : error;
    }

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
    /// <param name="user">The persisted account returned by the user query, or null before first provisioning.</param>
    /// <param name="saveError">The failure returned by each save, or no failure.</param>
    /// <param name="beforeSave">An optional cancellation-aware barrier controlling overlapping save attempts.</param>
    /// <remarks>Only the Users set is supported; all unrelated DbSet properties deliberately throw NotSupportedException.</remarks>
    private sealed class ProvisioningContext(UserAccount? user, Exception? saveError = null,
        Func<CancellationToken, Task>? beforeSave = null) : ISidequestDbContext
    {
        private readonly UserSet users = new(user);
        /// <summary>Gets the mutable local account queried by this attempt.</summary>
        public UserAccount User => users.CurrentUser ?? throw new InvalidOperationException("No account has been added or queried.");
        /// <summary>Gets the number of new local accounts tracked by this attempt.</summary>
        public int AddedUsers => users.AddedUsers;
        /// <inheritdoc/>
        public DbSet<UserAccount> Users => users;
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

        /// <inheritdoc />
        public Task<UserAccount?> FindUserForUpdateAsync(Guid tenantId, Guid objectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(IsolationLevel.Serializable, Isolation);
            return users.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ObjectId == objectId, cancellationToken);
        }

        /// <inheritdoc/>
        /// <remarks>Records a save attempt and returns the configured failure or a successful affected-row count.</remarks>
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (beforeSave is not null) await beforeSave(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (saveError is not null) throw saveError;
            return 1;
        }

        /// <inheritdoc/>
        public Task LockEventAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not acquire Event locks.");

        /// <inheritdoc />
        public Task<MembershipRequestState> ReadMembershipRequestStateForUpdateAsync(Guid eventId, Guid userId,
            DateTimeOffset since, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not request Event membership.");

        /// <inheritdoc/>
        public Task<QuestInvitation?> FindQuestInvitationForUpdateAsync(Guid questId, Guid userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not grant Quest invitations.");

        /// <inheritdoc/>
        public Task<QuestParticipation?> FindQuestParticipationForUpdateAsync(Guid questId, Guid userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not change Quest participation.");

        /// <inheritdoc/>
        public Task<CalendarDeliveryState?> FindCalendarDeliveryStateForUpdateAsync(Guid questId, Guid userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not change calendar intent.");

        /// <inheritdoc/>
        public Task<bool> HasNotificationForUpdateAsync(Guid sourceChangeId, Guid userId, NotificationKind kind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not produce inbox effects.");

        /// <inheritdoc/>
        public Task<List<ScheduledWork>> ReadReminderSchedulesForUpdateAsync(Guid questId, Guid userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not schedule reminders.");

        /// <inheritdoc/>
        public Task<bool> HasScheduledWorkForUpdateAsync(string deduplicationKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not schedule completion work.");

        /// <inheritdoc/>
        public Task<bool> HasPendingScheduledWorkForUpdateAsync(string deduplicationPrefix, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Account provisioning does not schedule completion work.");

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            await users.DisposeTrackingAsync();
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

    /// <summary>Adapts an optional account to scalar queries and records inserts with connection-free EF metadata tracking.</summary>
    /// <param name="user">The existing account exposed through LINQ-to-Objects, or null for an initially absent row.</param>
    private sealed class UserSet(UserAccount? user) : DbSet<UserAccount>, IQueryable<UserAccount>
    {
        private readonly IQueryable<UserAccount> query = (user is null ? Array.Empty<UserAccount>() : new[] { user }).AsQueryable();
        private readonly SidequestDbContext tracking = new(new DbContextOptionsBuilder<SidequestDbContext>().UseSqlServer().Options);
        /// <summary>Gets the existing or newly tracked account for this operation.</summary>
        public UserAccount? CurrentUser { get; private set; } = user;
        /// <summary>Gets the number of inserts attempted by the operation.</summary>
        public int AddedUsers { get; private set; }
        /// <inheritdoc/>
        public override EntityEntry<UserAccount> Add(UserAccount entity)
        {
            AddedUsers++;
            CurrentUser = entity;
            return tracking.Users.Add(entity);
        }
        /// <summary>Disposes metadata tracking; this context has no connection string and never executes SQL.</summary>
        /// <returns>Completion of tracker disposal.</returns>
        public ValueTask DisposeTrackingAsync() => tracking.DisposeAsync();
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
