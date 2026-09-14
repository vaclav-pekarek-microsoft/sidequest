namespace Sidequest.Domain.Model;

/// <summary>Private Quest invitation grant state; no acceptance or expiration workflow exists.</summary>
public enum QuestInvitationStatus
{
    /// <summary>Named access remains valid while Event membership and eligibility remain.</summary>
    Active,
    /// <summary>Invitation-derived access withdrawn; independent ownership access is unaffected.</summary>
    Revoked
}
