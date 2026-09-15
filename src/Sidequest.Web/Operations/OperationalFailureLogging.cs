using Sidequest.Application.Operations;

namespace Sidequest.Web.Operations;

internal static class OperationalFailureLogging
{
    internal static string Classification(OperationalObservationException exception) => exception.Kind switch
    {
        OperationalFailureKind.SchemaMismatch => "schema-mismatch",
        OperationalFailureKind.SchemaUnavailable => "schema-unavailable",
        OperationalFailureKind.StoreUnavailable => "sql-unavailable",
        OperationalFailureKind.Timeout => "timeout",
        OperationalFailureKind.Configuration => "configuration",
        _ => throw new ArgumentOutOfRangeException(nameof(exception))
    };
}
