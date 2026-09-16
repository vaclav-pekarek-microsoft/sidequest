namespace Sidequest.Web.Components.Quests;

/// <summary>Retains unsent management input in its owning page while authorized controls are temporarily removed for revalidation.</summary>
/// <param name="SelectedPerson">The selected person's identifier text, or an empty string when no person is selected; it never grants access.</param>
/// <param name="Reason">Unvalidated reason text retained only in circuit memory until successful submission, explicit reload, navigation, or confirmed access loss.</param>
public sealed record QuestManagementDraft(string SelectedPerson = "", string Reason = "");
