using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Domain.Rules;

namespace Sidequest.Infrastructure.Persistence;

internal static class SqlServerFailures
{
    internal static bool TryGetConflict(Exception exception, [NotNullWhen(true)] out DomainException? conflict)
    {
        conflict = null;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
                return false;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DomainException { Code: ErrorCode.Conflict } existing)
            {
                conflict = existing;
                return true;
            }

            if (current is DbUpdateConcurrencyException)
            {
                conflict = new(ErrorCode.Conflict, "This item changed. Reload and try again.");
                return true;
            }

            if (current is not SqlException sql)
                continue;

            if (sql.Errors.Cast<SqlError>().Any(error => error.Number == 1205))
            {
                conflict = new(ErrorCode.Conflict, "This operation conflicted with another change. Reload and try again.");
                return true;
            }

            if (sql.Errors.Cast<SqlError>().Any(error => error.Number is 2601 or 2627))
            {
                conflict = new(ErrorCode.Conflict,
                    "This operation conflicts with a change already saved. Reload and try again.");
                return true;
            }
        }

        return false;
    }

    internal static void ThrowIfConflict(Exception exception)
    {
        if (TryGetConflict(exception, out var conflict))
            throw conflict;
    }
}
