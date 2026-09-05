namespace OpenCode.Core.Database.Migrations;

using Microsoft.Data.Sqlite;

internal static class MigrationSql
{
    internal static async Task<List<T>> ReadAsync<T>(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, Func<SqliteDataReader, T> read, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(true);
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var result = new List<T>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using var readerLifetime = reader.ConfigureAwait(true);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(read(reader));
        return result;
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(true);
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
