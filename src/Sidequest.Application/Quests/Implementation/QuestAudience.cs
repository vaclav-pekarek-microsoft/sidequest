namespace Sidequest.Application.Quests.Implementation;

internal sealed record QuestAudience(Guid[] Owners, Guid[] Attendees, Guid[] Followers, Guid[] Invitees)
{
    internal Guid[] All => Owners.Concat(Attendees).Concat(Followers).Concat(Invitees).Distinct().ToArray();
}
