using Sidequest.Domain.Model;

namespace Sidequest.Domain.Rules;

public static class AccessRules
{
    public static bool CanReadEvent(bool eligible, bool member, bool owner, EventStatus status) =>
        eligible && (status == EventStatus.Draft ? owner : member);

    public static bool CanReadQuest(bool eligible, bool member, bool owner, bool invited,
        EventStatus eventStatus, QuestStatus questStatus, QuestVisibility visibility) =>
        eligible && member && eventStatus != EventStatus.Draft &&
        (questStatus == QuestStatus.Draft ? owner : owner || invited || visibility == QuestVisibility.Public);

    public static bool CanModerate(bool eligible, bool member, bool eventOwner, QuestStatus status) =>
        eligible && member && eventOwner && status != QuestStatus.Draft;
}
