namespace Sidequest.Domain.Model;

/// <summary>Mutually exclusive Quest/user participation, independent of ownership and invitations.</summary>
public enum ParticipationStatus
{
    /// <summary>Neither following nor attending.</summary>
    None,
    /// <summary>Receiving follower updates without attendance, calendar invitations, or attendee reminders.</summary>
    Following,
    /// <summary>Attending; replaces Following and enables attendee delivery subject to lifecycle and preferences.</summary>
    Joined
}
