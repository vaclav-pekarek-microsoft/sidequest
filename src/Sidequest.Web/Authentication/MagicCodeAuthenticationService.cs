using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;

namespace Sidequest.Web.Authentication;

/// <summary>Creates, delivers, verifies, and consumes SQL-backed Microsoft-alias sign-in challenges.</summary>
/// <param name="factory">Creates isolated contexts for challenge and account transactions.</param>
/// <param name="emailGateway">Delivers codes through the configured provider without exposing them to logs.</param>
/// <param name="settings">Validated account namespace and optional bootstrap mailbox.</param>
/// <param name="clock">Provides deterministic UTC challenge and sign-in timestamps.</param>
/// <param name="logger">Records bounded failure categories without aliases, addresses, codes, or challenge identifiers.</param>
public sealed class MagicCodeAuthenticationService(
    ISidequestDbContextFactory factory,
    IEmailGateway emailGateway,
    FoundationAuthenticationSettings settings,
    TimeProvider clock,
    ILogger<MagicCodeAuthenticationService> logger)
{
    private const int MaximumRequestsPerHour = 5;
    private const int MaximumAttempts = 5;
    private const int HashIterations = 100_000;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMinutes(1);

    /// <summary>Creates and attempts to deliver a one-time code for a normalized Microsoft alias.</summary>
    /// <param name="alias">User-entered alias without the fixed Microsoft domain.</param>
    /// <param name="cancellationToken">Cancels SQL or provider work.</param>
    /// <returns>A random challenge identifier. Throttled or failed delivery returns an indistinguishable decoy identifier.</returns>
    /// <exception cref="ArgumentException">The alias cannot form an exact Microsoft mailbox.</exception>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public async Task<Guid> RequestAsync(string? alias, CancellationToken cancellationToken)
    {
        var email = MagicAlias.Normalize(alias);
        var now = clock.GetUtcNow();
        await DeleteExpiredChallengesAsync(now, cancellationToken).ConfigureAwait(false);
        var challenge = await CreateChallengeAsync(email, now, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            logger.LogInformation("Magic-code request was throttled.");
            return Guid.NewGuid();
        }

        var code = challenge.Value.Code;
        var message = new EmailMessage(
            email,
            "Your Sidequest sign-in code",
            $"<p>Your Sidequest sign-in code is:</p><p><strong>{code}</strong></p><p>This code expires in 10 minutes and can be used once.</p>",
            $"Your Sidequest sign-in code is {code}. It expires in 10 minutes and can be used once.",
            $"magic-signin:{challenge.Value.Id:N}");
        try
        {
            await emailGateway.SendAsync(message, cancellationToken).ConfigureAwait(false);
            return challenge.Value.Id;
        }
        catch (DeliveryTransportException exception)
        {
            logger.LogWarning("Magic-code delivery failed ({Outcome}); the browser received a decoy challenge.",
                exception.Outcome);
            return Guid.NewGuid();
        }
    }

    /// <summary>Consumes a valid challenge and returns the persisted account principal for an application cookie.</summary>
    /// <param name="challengeId">Random challenge identifier retained by the requesting browser.</param>
    /// <param name="code">Six-digit code supplied from the Microsoft mailbox.</param>
    /// <param name="cancellationToken">Cancels SQL work.</param>
    /// <returns>An authenticated principal on success; null for any expected verification or account-link failure.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public async Task<ClaimsPrincipal?> VerifyAsync(
        Guid challengeId, string? code, CancellationToken cancellationToken)
    {
        if (challengeId == Guid.Empty)
            return null;

        var now = clock.GetUtcNow();
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var challenge = await db.FindMagicSignInChallengeForUpdateAsync(challengeId, cancellationToken)
            .ConfigureAwait(false);
        if (challenge is null || challenge.ConsumedUtc is not null ||
            challenge.ExpiresUtc <= now || challenge.AttemptCount >= MaximumAttempts)
            return null;

        challenge.AttemptCount++;
        if (!ValidCode(code, challenge.CodeSalt, challenge.CodeHash))
        {
            if (challenge.AttemptCount >= MaximumAttempts)
                challenge.ConsumedUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Magic-code verification failed.");
            return null;
        }

        var matches = await db.Users
            .Where(user => user.TenantId == settings.TenantId && user.Email.ToLower() == challenge.Email)
            .Take(2)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (matches.Count > 1)
        {
            challenge.ConsumedUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Magic-code account linking failed because the verified address is ambiguous.");
            return null;
        }

        var user = matches.SingleOrDefault();
        var isNew = user is null;
        if (user is null)
        {
            var objectId = CreateObjectId(settings.TenantId, challenge.Email);
            user = await db.FindUserForUpdateAsync(settings.TenantId, objectId, cancellationToken).ConfigureAwait(false);
            if (user is not null && !string.Equals(user.Email, challenge.Email, StringComparison.OrdinalIgnoreCase))
            {
                challenge.ConsumedUtc = now;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning("Magic-code account linking failed because the generated identity is already reserved.");
                return null;
            }
            if (user is null)
            {
                user = new UserAccount
                {
                    TenantId = settings.TenantId,
                    ObjectId = objectId,
                    DisplayName = MagicAlias.LocalPart(challenge.Email),
                    Email = challenge.Email
                };
                db.Users.Add(user);
            }
        }

        if (!user.IsEligible || user.DepartureVerifiedUtc is not null)
        {
            challenge.ConsumedUtc = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Magic-code account linking rejected an ineligible persisted account.");
            return null;
        }

        user.LastSignedInUtc = now;
        challenge.ConsumedUtc = now;
        if (isNew && string.Equals(settings.BootstrapAdministratorEmail, challenge.Email, StringComparison.Ordinal))
            db.Administrators.Add(new Administrator { UserId = user.Id });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CreatePrincipal(user, challenge.Email);
    }

    /// <summary>Creates the stable object identifier used for a new Microsoft mailbox in one account namespace.</summary>
    /// <param name="tenantId">Configured nonempty account namespace.</param>
    /// <param name="email">Normalized Microsoft mailbox.</param>
    /// <returns>A deterministic nonempty UUID-shaped identifier.</returns>
    public static Guid CreateObjectId(Guid tenantId, string email)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A nonempty account namespace is required.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (!string.Equals(MagicAlias.Normalize(MagicAlias.LocalPart(email)), email, StringComparison.Ordinal))
            throw new ArgumentException("A normalized Microsoft mailbox is required.", nameof(email));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:D}\n{email}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private async Task<(Guid Id, string Code)?> CreateChallengeAsync(
        string email, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var recent = await db.ReadMagicSignInRequestsForUpdateAsync(
            email, now.AddHours(-1), cancellationToken).ConfigureAwait(false);
        if (recent.Count >= MaximumRequestsPerHour ||
            (recent.Count > 0 && recent[0].CreatedUtc > now - MinimumRequestInterval))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var salt = RandomNumberGenerator.GetBytes(16);
        var challenge = new MagicSignInChallenge
        {
            Email = email,
            CodeSalt = salt,
            CodeHash = Hash(code, salt),
            CreatedUtc = now,
            ExpiresUtc = now + ChallengeLifetime
        };
        db.MagicSignInChallenges.Add(challenge);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (challenge.Id, code);
    }

    private async Task DeleteExpiredChallengesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await db.MagicSignInChallenges
            .Where(challenge => challenge.ExpiresUtc < now.AddDays(-1))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ValidCode(string? code, byte[] salt, byte[] expectedHash)
    {
        if (code is null || code.Length != 6 || code.Any(character => !char.IsAsciiDigit(character)) ||
            salt.Length != 16 || expectedHash.Length != 32)
            return false;
        return CryptographicOperations.FixedTimeEquals(Hash(code, salt), expectedHash);
    }

    private static byte[] Hash(string code, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(code, salt, HashIterations, HashAlgorithmName.SHA256, 32);

    private static ClaimsPrincipal CreatePrincipal(UserAccount user, string verifiedEmail) =>
        new(new ClaimsIdentity(
        [
            new("tid", user.TenantId.ToString()),
            new("oid", user.ObjectId.ToString()),
            new("name", user.DisplayName),
            new("preferred_username", verifiedEmail),
            new("roles", FoundationAuthenticationSettings.MagicCodeRole),
            new(FoundationAuthenticationSettings.MagicCodeClaim, "true")
        ], FoundationAuthenticationSettings.CookieScheme, "name", "roles"));
}
