namespace Sidequest.Web.Components.Quests;

/// <summary>User-confirmed management intent, not an authorization or trusted service command.</summary>
/// <param name="Action">Allowlisted UI action selected by the management controls.</param>
/// <param name="UserId">Internal target account for invitation, attendance, or ownership actions; null for lifecycle actions.</param>
/// <param name="Reason">User explanation validated by the authoritative application service.</param>
public sealed record QuestActionRequest(string Action, Guid? UserId, string Reason);
