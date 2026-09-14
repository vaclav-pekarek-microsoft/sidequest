using System.Text.RegularExpressions;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Directory;

/// <summary>Deployment-only tenant and approved eligibility policy for the real Graph directory adapter.</summary>
/// <remarks>No defaults establish workforce eligibility. Tenant owners must approve and protect the extension's
/// authoritative population process and grant the least Graph permissions needed to read it and expand groups.</remarks>
public sealed class GraphDirectoryOptions
{
    /// <summary>The single workforce tenant; empty configuration fails closed.</summary>
    public Guid TenantId { get; init; }

    /// <summary>Approved directory extension property indicating workforce admission, for example an organization-defined extension.</summary>
    public string WorkforceExtension { get; init; } = "";

    /// <summary>Exact approved string or JSON-boolean representation required in the extension; no organizational value is assumed.</summary>
    public string WorkforceValue { get; init; } = "";

    /// <summary>Must be explicitly enabled only after tenant approval of the extension's workforce-only policy.</summary>
    public bool WorkforcePolicyApproved { get; init; }

    /// <summary>Maximum complete expansion recipient count before the adapter fails without returning partial data.</summary>
    public int MaximumRecipients { get; init; } = 5000;

    /// <summary>Maximum pages accepted from a single directory enumeration, preventing unbounded or cyclic pagination.</summary>
    public int MaximumPages { get; init; } = 1000;

    /// <summary>Maximum 429/503 retries for one request; each waits for Graph's Retry-After before retrying.</summary>
    public int MaximumThrottlingRetries { get; init; } = 3;

    internal void Validate()
    {
        if (TenantId == Guid.Empty || !WorkforcePolicyApproved ||
            !Regex.IsMatch(WorkforceExtension, "^extension_[A-Za-z0-9_]{1,200}$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(WorkforceValue) || WorkforceValue.Length > 256 ||
            MaximumRecipients is < 1 or > 100000 || MaximumPages is < 1 or > 10000 ||
            MaximumThrottlingRetries is < 0 or > 8)
            throw new DomainException(ErrorCode.DependencyUnavailable,
                "Directory workforce policy is not configured or approved. Contact the application operator.");
    }
}
