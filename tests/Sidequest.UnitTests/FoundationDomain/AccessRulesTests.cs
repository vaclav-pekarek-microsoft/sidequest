using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.UnitTests.FoundationDomain;

/// <summary>Exercises explicit eligibility, membership, ownership, invitation, and lifecycle access partitions.</summary>
public sealed class AccessRulesTests
{
    private static readonly EventStatus[] PublishedEvents =
        [EventStatus.Active, EventStatus.Completed, EventStatus.Cancelled, EventStatus.Archived];

    private static readonly QuestStatus[] PublishedQuests =
        [QuestStatus.Active, QuestStatus.Suspended, QuestStatus.Completed, QuestStatus.Cancelled, QuestStatus.Archived];

    /// <summary>Builds Event read cases, including the required denial of Draft owners without membership.</summary>
    /// <returns>Eligibility, membership, ownership, Event status, and independently specified permission rows.</returns>
    public static TheoryData<bool, bool, bool, EventStatus, bool> EventCases()
    {
        var data = new TheoryData<bool, bool, bool, EventStatus, bool>();
        // Literal policy table: eligibility and membership are mandatory even for a Draft owner.
        (bool Eligible, bool Member, bool Owner, bool Draft, bool Published)[] rights =
        [
            (true, true, true, true, true),
            (true, true, false, false, true),
            (true, false, true, false, false),
            (true, false, false, false, false),
            (false, true, true, false, false),
            (false, true, false, false, false),
            (false, false, true, false, false),
            (false, false, false, false, false)
        ];
        foreach (var row in rights)
        {
            data.Add(row.Eligible, row.Member, row.Owner, EventStatus.Draft, row.Draft);
            foreach (var status in PublishedEvents)
                data.Add(row.Eligible, row.Member, row.Owner, status, row.Published);
        }
        return data;
    }

    /// <summary>Checks Event read permissions without treating ownership as a substitute for individual membership.</summary>
    /// <param name="eligible">Whether the account is eligible for application access.</param>
    /// <param name="member">Whether active individual Event membership exists.</param>
    /// <param name="owner">Whether an Event owner relation exists.</param>
    /// <param name="status">The Event lifecycle state.</param>
    /// <param name="expected">The permission required by the literal policy table.</param>
    [Theory]
    [MemberData(nameof(EventCases))]
    public void CanReadEvent_ExplicitStatusAndRightsMatrix(
        bool eligible, bool member, bool owner, EventStatus status, bool expected)
    {
        // Retain the Draft/eligible/nonmember/owner denial as a failing regression if bypassed.
        Assert.Equal(expected, AccessRules.CanReadEvent(eligible, member, owner, status));
    }

    /// <summary>Builds ordinary Quest-read cases beneath every non-Draft Event status.</summary>
    /// <returns>Account gates, Quest grants, lifecycle states, visibility, and literal expected permissions.</returns>
    public static TheoryData<bool, bool, bool, bool, EventStatus, QuestStatus, QuestVisibility, bool> QuestCases()
    {
        var data = new TheoryData<bool, bool, bool, bool, EventStatus, QuestStatus, QuestVisibility, bool>();
        // Expectations are a policy table, not a reproduction of the production boolean expression.
        (QuestVisibility Visibility, bool Owner, bool Invited, bool Draft, bool Published)[] rights =
        [
            (QuestVisibility.Public, false, false, false, true),
            (QuestVisibility.Public, false, true, false, true),
            (QuestVisibility.Public, true, false, true, true),
            (QuestVisibility.Public, true, true, true, true),
            (QuestVisibility.Private, false, false, false, false),
            (QuestVisibility.Private, false, true, false, true),
            (QuestVisibility.Private, true, false, true, true),
            (QuestVisibility.Private, true, true, true, true)
        ];
        foreach (var parent in PublishedEvents)
        foreach (var row in rights)
        {
            AddGates(QuestStatus.Draft, row.Draft);
            foreach (var status in PublishedQuests)
                AddGates(status, row.Published);

            void AddGates(QuestStatus status, bool eligibleMemberExpected)
            {
                data.Add(true, true, row.Owner, row.Invited, parent, status, row.Visibility, eligibleMemberExpected);
                data.Add(false, true, row.Owner, row.Invited, parent, status, row.Visibility, false);
                data.Add(true, false, row.Owner, row.Invited, parent, status, row.Visibility, false);
                data.Add(false, false, row.Owner, row.Invited, parent, status, row.Visibility, false);
            }
        }
        return data;
    }

    /// <summary>Checks the intersections of Quest visibility, lifecycle, ownership, invitations, and account gates.</summary>
    /// <param name="eligible">Whether the account remains eligible.</param>
    /// <param name="member">Whether active parent Event membership exists.</param>
    /// <param name="owner">Whether the caller owns this Quest, independently of Event ownership.</param>
    /// <param name="invited">Whether an active private Quest invitation exists.</param>
    /// <param name="eventStatus">The parent Event lifecycle state.</param>
    /// <param name="questStatus">The Quest lifecycle state.</param>
    /// <param name="visibility">The Quest's public or private visibility.</param>
    /// <param name="expected">The permission required by the explicit policy row.</param>
    [Theory]
    [MemberData(nameof(QuestCases))]
    public void CanReadQuest_ExplicitVisibilityStatusAndInvitationMatrix(
        bool eligible, bool member, bool owner, bool invited,
        EventStatus eventStatus, QuestStatus questStatus, QuestVisibility visibility, bool expected)
    {
        Assert.Equal(expected,
            AccessRules.CanReadQuest(eligible, member, owner, invited, eventStatus, questStatus, visibility));
    }

    /// <summary>Builds account and Quest-grant combinations that must not bypass a Draft parent.</summary>
    /// <returns>Eligibility, membership, Quest ownership, invitation, status, and visibility denial rows.</returns>
    public static TheoryData<bool, bool, bool, bool, QuestStatus, QuestVisibility> DraftParentCases()
    {
        var data = new TheoryData<bool, bool, bool, bool, QuestStatus, QuestVisibility>();
        QuestStatus[] statuses = [QuestStatus.Draft, .. PublishedQuests];
        foreach (var status in statuses)
        foreach (var visibility in new[] { QuestVisibility.Public, QuestVisibility.Private })
        foreach (var owner in new[] { false, true })
        foreach (var invited in new[] { false, true })
        {
            data.Add(true, true, owner, invited, status, visibility);
            data.Add(false, true, owner, invited, status, visibility);
            data.Add(true, false, owner, invited, status, visibility);
            data.Add(false, false, owner, invited, status, visibility);
        }
        return data;
    }

    /// <summary>Checks defensive ordinary-read denial for SQL-representable Quests beneath a Draft Event.</summary>
    /// <param name="eligible">Whether the account is eligible.</param>
    /// <param name="member">Whether active Event membership exists.</param>
    /// <param name="owner">Whether the caller owns the Quest.</param>
    /// <param name="invited">Whether an active Quest invitation exists.</param>
    /// <param name="status">The child Quest lifecycle state.</param>
    /// <param name="visibility">The child Quest visibility.</param>
    [Theory]
    [MemberData(nameof(DraftParentCases))]
    public void CanReadQuest_DraftParent_DeniesRegardlessOfQuestRights(
        bool eligible, bool member, bool owner, bool invited, QuestStatus status, QuestVisibility visibility)
    {
        Assert.False(AccessRules.CanReadQuest(eligible, member, owner, invited, EventStatus.Draft, status, visibility));
    }

    /// <summary>Builds each missing moderation gate and the permitted non-Draft lifecycle partitions.</summary>
    /// <returns>Eligibility, membership, Event ownership, Quest status, and expected moderation permission.</returns>
    public static TheoryData<bool, bool, bool, QuestStatus, bool> ModerationCases()
    {
        var data = new TheoryData<bool, bool, bool, QuestStatus, bool>();
        QuestStatus[] statuses = [QuestStatus.Draft, .. PublishedQuests];
        foreach (var status in statuses)
        {
            data.Add(false, true, true, status, false);
            data.Add(true, false, true, status, false);
            data.Add(true, true, false, status, false);
            data.Add(false, false, true, status, false);
            data.Add(false, true, false, status, false);
            data.Add(true, false, false, status, false);
            data.Add(false, false, false, status, false);
        }
        data.Add(true, true, true, QuestStatus.Draft, false);
        foreach (var status in PublishedQuests)
            data.Add(true, true, true, status, true);
        return data;
    }

    /// <summary>Checks that moderation requires all account/ownership gates and never exposes Quest drafts.</summary>
    /// <param name="eligible">Whether the moderator is eligible.</param>
    /// <param name="member">Whether active individual Event membership exists.</param>
    /// <param name="eventOwner">Whether the caller owns the parent Event.</param>
    /// <param name="status">The Quest state considered for moderation.</param>
    /// <param name="expected">The permission required by the explicit moderation row.</param>
    [Theory]
    [MemberData(nameof(ModerationCases))]
    public void CanModerate_RequiresEligibilityMembershipAndEventOwnerForNonDraft(
        bool eligible, bool member, bool eventOwner, QuestStatus status, bool expected)
    {
        Assert.Equal(expected, AccessRules.CanModerate(eligible, member, eventOwner, status));
    }
}
