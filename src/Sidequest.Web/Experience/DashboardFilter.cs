using Sidequest.Application.Quests;

namespace Sidequest.Web.Experience;

/// <summary>Immutable dashboard selection; dates are civil days in the selected Event zone or labeled UTC across Events.</summary>
/// <param name="Kind">Authorized list category; Joined and Following remain exclusive in the application service.</param>
/// <param name="EventId">Optional parent Event filter, not an access grant.</param>
/// <param name="From">Inclusive first start date, or no lower bound.</param>
/// <param name="Through">Inclusive last start date, converted to an exclusive next-day boundary.</param>
/// <param name="PageSize">Page size, validated by existing PageRequest rules (1–100).</param>
public sealed record DashboardFilter(QuestListKind Kind, Guid? EventId, DateOnly? From, DateOnly? Through, int PageSize = 25);
