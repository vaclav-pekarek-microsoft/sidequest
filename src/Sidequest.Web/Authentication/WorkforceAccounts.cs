using System.Data;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Authentication;

/// <summary>Provisions admitted identities and rechecks persisted session eligibility using operation-scoped contexts.</summary>
/// <param name="factory">Creates a fresh database context for each operation or provisioning retry.</param>
/// <param name="settings">The allowed identity claims and optional bootstrap administrator object.</param>
/// <param name="clock">Provides UTC sign-in timestamps and session-expiry comparisons.</param>
/// <param name="logger">Records safe provisioning retry diagnostics without token or contact data.</param>
/// <remarks>Operations own separate contexts. Callers must not concurrently mutate supplied principals or tracked accounts.</remarks>
public sealed class WorkforceAccounts(
    ISidequestDbContextFactory factory, FoundationAuthenticationSettings settings,
    TimeProvider clock, ILogger<WorkforceAccounts> logger)
{
    /// <summary>Creates or updates an eligible local account before authentication issues its session cookie.</summary>
    /// <param name="principal">The validated Entra principal or loopback development principal to admit.</param>
    /// <param name="cancellationToken">Cancels SQL operations or the bounded delay between retry attempts.</param>
    /// <returns>A task completing after the serializable provisioning transaction commits.</returns>
    /// <exception cref="DomainException">Admission is forbidden, the account is disabled/departed, or persistence rejects the operation.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Normalized conflicts and EF-wrapped conflicts retry twice with fresh contexts. Only first provisioning may grant the explicitly configured administrator role.</remarks>
    /// <example>
    /// <code>
    /// // The authentication handler has already validated the principal.
    /// await accounts.ProvisionAsync(principal, cancellationToken);
    /// WorkforceSession.Stamp(principal, clock.GetUtcNow());
    /// // Issue the application cookie only after provisioning succeeds.
    /// </code>
    /// </example>
    public async Task ProvisionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var identity = WorkforceIdentity.Read(principal, settings)
            ?? throw new DomainException(ErrorCode.Forbidden, "This identity is not eligible for Sidequest.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
                var user = await db.Users.SingleOrDefaultAsync(
                    u => u.TenantId == identity.TenantId && u.ObjectId == identity.ObjectId, cancellationToken).ConfigureAwait(false);
                var isNew = user is null;
                if (user is null)
                {
                    user = new UserAccount { TenantId = identity.TenantId, ObjectId = identity.ObjectId };
                    db.Users.Add(user);
                }
                UpdateContact(user, identity, clock.GetUtcNow());
                // Bootstrap is one-time provisioning, never a recurring privilege grant on login.
                if (isNew && settings.BootstrapAdministratorObjectId == identity.ObjectId)
                    db.Administrators.Add(new Administrator { UserId = user.Id });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < 2 && IsRetryableConflict(exception))
            {
                logger.LogWarning("Retrying concurrent account provisioning (attempt {Attempt}).", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(75 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Updates display/contact information and the sign-in timestamp without changing identity or eligibility.</summary>
    /// <param name="user">The tracked local account; the caller is responsible for matching its tenant/object key.</param>
    /// <param name="identity">The admitted identity supplying current display name and email.</param>
    /// <param name="now">The UTC instant of successful sign-in.</param>
    /// <exception cref="ArgumentNullException"><paramref name="user"/> or <paramref name="identity"/> is null.</exception>
    /// <exception cref="DomainException">The account is disabled or has verified departure; no fields are changed.</exception>
    public static void UpdateContact(UserAccount user, UserIdentity identity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(identity);
        if (!user.IsEligible || user.DepartureVerifiedUtc is not null)
            throw new DomainException(ErrorCode.Forbidden, "This local account is disabled. Contact an administrator.");
        user.DisplayName = identity.DisplayName;
        user.Email = identity.Email;
        user.LastSignedInUtc = now;
    }

    /// <summary>Checks session expiry, workforce claims, and current local eligibility without modifying account data.</summary>
    /// <param name="principal">The session principal to revalidate.</param>
    /// <param name="cancellationToken">Cancels the database lookup.</param>
    /// <returns>Whether the unexpired admitted identity maps to an eligible, non-departed local account.</returns>
    /// <exception cref="OperationCanceledException">The database lookup was canceled.</exception>
    /// <remarks>Database failures propagate so authentication callers can invalidate the session rather than assume access.</remarks>
    public async Task<bool> IsEligibleAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var identity = WorkforceIdentity.Read(principal, settings);
        if (identity is null || !WorkforceSession.IsCurrent(principal, clock.GetUtcNow())) return false;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        return await db.Users.AsNoTracking().AnyAsync(u => u.TenantId == identity.TenantId &&
            u.ObjectId == identity.ObjectId && u.IsEligible && u.DepartureVerifiedUtc == null, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRetryableConflict(Exception exception) =>
        exception is DomainException { Code: ErrorCode.Conflict } ||
        exception is DbUpdateException { InnerException: DomainException { Code: ErrorCode.Conflict } } ||
        exception is DbUpdateConcurrencyException ||
        exception is SqlException { Number: 1205 or 2601 or 2627 } ||
        exception is DbUpdateException { InnerException: SqlException { Number: 1205 or 2601 or 2627 } };
}
