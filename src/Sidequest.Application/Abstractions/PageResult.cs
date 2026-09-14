namespace Sidequest.Application.Abstractions;

/// <summary>Authorized paged projection with a total count over the same filtered result set.</summary>
/// <typeparam name="T">Projection type for each visible item.</typeparam>
/// <param name="Items">Visible items in the requested page; empty when no items match.</param>
/// <param name="TotalCount">Number of authorized matching items before paging, not a count of undisclosed resources.</param>
/// <param name="Page">One-based page number represented by the result.</param>
/// <param name="PageSize">Validated maximum number of items per page.</param>
/// <remarks>The list is exposed read-only, not defensively copied. Thread safety depends on its backing collection and item types;
/// producers must not mutate a published result while consumers read it.</remarks>
public sealed record PageResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
