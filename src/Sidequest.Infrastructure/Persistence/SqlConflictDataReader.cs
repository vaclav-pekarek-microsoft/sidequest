using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;

namespace Sidequest.Infrastructure.Persistence;

/// <summary>Normalizes provider conflicts raised after command execution while EF consumes or closes a result reader.</summary>
/// <param name="reader">The provider reader whose cursor, values, cancellation, and lifetime remain authoritative.</param>
internal sealed class SqlConflictDataReader(DbDataReader reader) : DbDataReader, IDbColumnSchemaGenerator
{
    /// <inheritdoc />
    public override int Depth => Execute(() => reader.Depth);
    /// <inheritdoc />
    public override int FieldCount => Execute(() => reader.FieldCount);
    /// <inheritdoc />
    public override bool HasRows => Execute(() => reader.HasRows);
    /// <inheritdoc />
    public override bool IsClosed => reader.IsClosed;
    /// <inheritdoc />
    public override int RecordsAffected => Execute(() => reader.RecordsAffected);
    /// <inheritdoc />
    public override int VisibleFieldCount => Execute(() => reader.VisibleFieldCount);
    /// <inheritdoc />
    public override object this[int ordinal] => Execute(() => reader[ordinal]);
    /// <inheritdoc />
    public override object this[string name] => Execute(() => reader[name]);

    /// <inheritdoc />
    public override bool Read() => Execute(reader.Read);
    /// <inheritdoc />
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => reader.ReadAsync(cancellationToken));
    /// <inheritdoc />
    public override bool NextResult() => Execute(reader.NextResult);
    /// <inheritdoc />
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => reader.NextResultAsync(cancellationToken));
    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => Execute(() => reader.IsDBNull(ordinal));
    /// <inheritdoc />
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        ExecuteAsync(() => reader.IsDBNullAsync(ordinal, cancellationToken));
    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => Execute(() => reader.GetBoolean(ordinal));
    /// <inheritdoc />
    public override byte GetByte(int ordinal) => Execute(() => reader.GetByte(ordinal));
    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        Execute(() => reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length));
    /// <inheritdoc />
    public override char GetChar(int ordinal) => Execute(() => reader.GetChar(ordinal));
    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        Execute(() => reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length));
    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) => Execute(() => reader.GetDateTime(ordinal));
    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => Execute(() => reader.GetDecimal(ordinal));
    /// <inheritdoc />
    public override double GetDouble(int ordinal) => Execute(() => reader.GetDouble(ordinal));
    /// <inheritdoc />
    public override float GetFloat(int ordinal) => Execute(() => reader.GetFloat(ordinal));
    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => Execute(() => reader.GetGuid(ordinal));
    /// <inheritdoc />
    public override short GetInt16(int ordinal) => Execute(() => reader.GetInt16(ordinal));
    /// <inheritdoc />
    public override int GetInt32(int ordinal) => Execute(() => reader.GetInt32(ordinal));
    /// <inheritdoc />
    public override long GetInt64(int ordinal) => Execute(() => reader.GetInt64(ordinal));
    /// <inheritdoc />
    public override string GetString(int ordinal) => Execute(() => reader.GetString(ordinal));
    /// <inheritdoc />
    public override object GetValue(int ordinal) => Execute(() => reader.GetValue(ordinal));
    /// <inheritdoc />
    public override int GetValues(object[] values) => Execute(() => reader.GetValues(values));
    /// <inheritdoc />
    public override T GetFieldValue<T>(int ordinal) => Execute(() => reader.GetFieldValue<T>(ordinal));
    /// <inheritdoc />
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        ExecuteAsync(() => reader.GetFieldValueAsync<T>(ordinal, cancellationToken));
    /// <inheritdoc />
    public override string GetName(int ordinal) => Execute(() => reader.GetName(ordinal));
    /// <inheritdoc />
    public override int GetOrdinal(string name) => Execute(() => reader.GetOrdinal(name));
    /// <inheritdoc />
    public override Type GetFieldType(int ordinal) => Execute(() => reader.GetFieldType(ordinal));
    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) => Execute(() => reader.GetDataTypeName(ordinal));
    /// <inheritdoc />
    public override Type GetProviderSpecificFieldType(int ordinal) => Execute(() => reader.GetProviderSpecificFieldType(ordinal));
    /// <inheritdoc />
    public override object GetProviderSpecificValue(int ordinal) => Execute(() => reader.GetProviderSpecificValue(ordinal));
    /// <inheritdoc />
    public override int GetProviderSpecificValues(object[] values) => Execute(() => reader.GetProviderSpecificValues(values));
    /// <inheritdoc />
    public override DataTable? GetSchemaTable() => Execute(reader.GetSchemaTable);
    /// <inheritdoc />
    public override Task<DataTable?> GetSchemaTableAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => reader.GetSchemaTableAsync(cancellationToken));
    /// <inheritdoc />
    public ReadOnlyCollection<DbColumn> GetColumnSchema() => Execute(reader.GetColumnSchema);
    /// <inheritdoc />
    public override Task<ReadOnlyCollection<DbColumn>> GetColumnSchemaAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => reader.GetColumnSchemaAsync(cancellationToken));
    /// <inheritdoc />
    public override Stream GetStream(int ordinal) => Execute(() => reader.GetStream(ordinal));
    /// <inheritdoc />
    public override TextReader GetTextReader(int ordinal) => Execute(() => reader.GetTextReader(ordinal));
    /// <inheritdoc />
    protected override DbDataReader GetDbDataReader(int ordinal) =>
        new SqlConflictDataReader(Execute(() => reader.GetData(ordinal)));
    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => Enumerate().GetEnumerator();
    /// <inheritdoc />
    public override void Close() => Execute(reader.Close);
    /// <inheritdoc />
    public override Task CloseAsync() => ExecuteAsync(reader.CloseAsync);
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Execute(reader.Dispose);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private IEnumerable Enumerate()
    {
        var enumerator = Execute(reader.GetEnumerator);
        try
        {
            while (Execute(enumerator.MoveNext))
            {
                yield return Execute(() => enumerator.Current);
            }
        }
        finally
        {
            if (enumerator is IDisposable disposable)
            {
                Execute(disposable.Dispose);
            }
        }
    }

    private static T Execute<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private static void Execute(Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }

    private static async Task ExecuteAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlServerFailures.TryGetConflict(exception, out var conflict))
        {
            throw conflict;
        }
    }
}
