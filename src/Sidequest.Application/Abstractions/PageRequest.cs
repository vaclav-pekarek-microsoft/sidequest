using Sidequest.Domain.Rules;

namespace Sidequest.Application.Abstractions;

/// <summary>One-based paging input validated when its database offset or limit is evaluated.</summary>
/// <param name="Page">One-based page number; must be positive and yield an offset within Int32.MaxValue.</param>
/// <param name="PageSize">Requested item count per page, inclusive range 1 through 100.</param>
public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    /// <summary>Zero-based number of rows to skip, calculated without integer overflow.</summary>
    /// <exception cref="DomainException">Page size is invalid, page is below one, or the calculated offset exceeds Int32.MaxValue (Validation).</exception>
    public int Offset
    {
        get
        {
            var offset = ((long)Page - 1) * Limit;
            if (Page < 1 || offset > int.MaxValue)
                throw new DomainException(ErrorCode.Validation, "Page is outside the supported range.", "Page");
            return (int)offset;
        }
    }

    /// <summary>Validated page size without silently clamping invalid input.</summary>
    /// <exception cref="DomainException">PageSize is outside 1 through 100 (Validation).</exception>
    public int Limit => PageSize is >= 1 and <= 100 ? PageSize
        : throw new DomainException(ErrorCode.Validation, "Page size must be between 1 and 100.", "PageSize");
}
