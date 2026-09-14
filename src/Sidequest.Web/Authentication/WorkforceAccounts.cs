using System.Data;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Authentication;

public sealed class WorkforceAccounts(
    ISidequestDbContextFactory factory, FoundationAuthenticationSettings settings,
    TimeProvider clock, ILogger<WorkforceAccounts> logger)
{
    public async Task ProvisionAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var identity = WorkforceIdentity.Read(principal, settings)
            ?? throw new DomainException(ErrorCode.Forbidden, "This identity is not eligible for Sidequest.");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var db = await factory.CreateAsync(cancellationToken);
                await using var transaction = await db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                var user = await db.Users.SingleOrDefaultAsync(
                    u => u.TenantId == identity.TenantId && u.ObjectId == identity.ObjectId, cancellationToken);
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
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < 2 && IsRetryableConflict(exception))
            {
                logger.LogWarning("Retrying concurrent account provisioning (attempt {Attempt}).", attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(75 * (attempt + 1)), cancellationToken);
            }
        }
    }

    public static void UpdateContact(UserAccount user, UserIdentity identity, DateTimeOffset now)
    {
        if (!user.IsEligible || user.DepartureVerifiedUtc is not null)
            throw new DomainException(ErrorCode.Forbidden, "This local account is disabled. Contact an administrator.");
        user.DisplayName = identity.DisplayName;
        user.Email = identity.Email;
        user.LastSignedInUtc = now;
    }

    public async Task<bool> IsEligibleAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var identity = WorkforceIdentity.Read(principal, settings);
        if (identity is null || !WorkforceSession.IsCurrent(principal, clock.GetUtcNow())) return false;
        await using var db = await factory.CreateAsync(cancellationToken);
        return await db.Users.AsNoTracking().AnyAsync(u => u.TenantId == identity.TenantId &&
            u.ObjectId == identity.ObjectId && u.IsEligible && u.DepartureVerifiedUtc == null, cancellationToken);
    }

    private static bool IsRetryableConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException ||
        exception is SqlException { Number: 1205 or 2601 or 2627 } ||
        exception is DbUpdateException { InnerException: SqlException { Number: 1205 or 2601 or 2627 } };
}
