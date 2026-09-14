namespace Sidequest.Domain.Model;

/// <summary>Authoritative individual Event membership state.</summary>
public enum MembershipStatus
{
    /// <summary>Membership grants access subject to eligibility and lifecycle checks.</summary>
    Active,
    /// <summary>Membership deactivated; restoration never restores prior Quest participation.</summary>
    Removed
}
