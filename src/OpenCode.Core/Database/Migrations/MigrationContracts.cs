namespace OpenCode.Core.Database.Migrations;

using System.Data;
using Microsoft.Data.Sqlite;
using OpenCode.Schema;

public enum MigrationJournalKind { Canonical, DrizzleNamed, DrizzleTimestamp }
public sealed record MigrationPlan(MigrationJournalKind Journal, string Baseline, IReadOnlyList<string> Completed, IReadOnlyList<SourceMigration> Pending)
{
    public string Destination => Pending.Count == 0 ? Baseline : Pending[^1].Id;
}
public sealed record MigrationResult(string PreviousBaseline, string CurrentBaseline, MigrationJournalKind PreviousJournal,
    IReadOnlyList<string> Applied, bool JournalConverted);

public sealed class MigrationRejectedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class SourceMigrationFailedException(string migrationId, Exception inner)
    : InvalidOperationException($"Source migration failed: {migrationId}. The upgrade transaction was not committed.", inner)
{
    public string MigrationId { get; } = migrationId;
}

/// <summary>An explicit owner-selected dotnet-channel path. This class never opens,
/// searches for, copies, or creates a database. Memory connections are caller-owned.</summary>
public sealed class MigrationTarget
{
    public string Path { get; }
    public MigrationTarget(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(selectedPath);
        Path = selectedPath == ":memory:" ? selectedPath : System.IO.Path.GetFullPath(selectedPath);
        if (Path != ":memory:" && !string.Equals(System.IO.Path.GetFileName(Path), OpenCodeChannel.DatabaseFileName,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new MigrationRejectedException("wrong_channel", $"Migration requires an explicitly selected {OpenCodeChannel.DatabaseFileName}. Other channel files and arbitrary legacy paths are not accepted.");
    }

    internal void RequireSelectedPath(string selectedPath)
    {
        var normalized = selectedPath == ":memory:" ? selectedPath : System.IO.Path.GetFullPath(selectedPath);
        if (!string.Equals(normalized, Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new MigrationRejectedException("target_mismatch", "The database path and explicit migration target differ. No connection was opened.");
    }

    internal void Require(SqliteConnection connection)
    {
        if (connection.State != ConnectionState.Open) throw new MigrationRejectedException("connection_not_open", "The database owner must supply an already-open connection before bootstrap classification.");
        var settings = new SqliteConnectionStringBuilder(connection.ConnectionString);
        var memory = settings.Mode == SqliteOpenMode.Memory || settings.DataSource == ":memory:";
        if (Path == ":memory:")
        {
            if (!memory) throw new MigrationRejectedException("target_mismatch", "The supplied connection is not the selected memory database.");
            return;
        }
        if (memory || !string.Equals(System.IO.Path.GetFullPath(connection.DataSource), Path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new MigrationRejectedException("target_mismatch", "The supplied connection does not match the owner-selected dotnet-channel path.");
    }
}
