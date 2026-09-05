namespace OpenCode.Core.Persistence;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

/// <summary>Operation-scoped mapping over an owner-opened connection. Never a schema owner.</summary>
internal sealed class PersistenceContext : DbContext
{
    internal PersistenceContext(SqliteConnection connection, SqliteTransaction? transaction = null)
        : base(new DbContextOptionsBuilder<PersistenceContext>()
            .UseSqlite(connection, contextOwnsConnection: false, sqlite => sqlite.UseRelationalNulls())
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options)
    {
        if (transaction is not null) Database.UseTransaction(transaction);
    }

    internal IQueryable<SessionRow> Sessions => Set<SessionRow>();
    internal IQueryable<MessageRow> Messages => Set<MessageRow>();
    internal IQueryable<InboxRow> Inbox => Set<InboxRow>();
    internal IQueryable<EventSequenceRow> Sequences => Set<EventSequenceRow>();
    internal IQueryable<EventRow> Events => Set<EventRow>();

    // Match the old SessionInfo reader: do not materialize unused numeric columns
    // (summary, claims, compaction) and introduce new decode failures for them.
    internal IQueryable<SessionRow> SessionDetails => Sessions.Select(row => new SessionRow
    {
        id = row.id, project_id = row.project_id, slug = row.slug, directory = row.directory, version = row.version,
        parent_id = row.parent_id, fork_session_id = row.fork_session_id, fork_boundary = row.fork_boundary,
        time_created = row.time_created, time_updated = row.time_updated, time_idle = row.time_idle,
        time_viewed = row.time_viewed, time_archived = row.time_archived,
        tokens_input = row.tokens_input, tokens_output = row.tokens_output, tokens_reasoning = row.tokens_reasoning,
        tokens_cache_read = row.tokens_cache_read, tokens_cache_write = row.tokens_cache_write, cost = row.cost,
        title = row.title, agent = row.agent, model = row.model, idle_outcome = row.idle_outcome,
        workspace_id = row.workspace_id, path = row.path, metadata = row.metadata, revert = row.revert,
    });

    internal Task<long> HighestProjectionAsync(string session, CancellationToken ct) =>
        SqliteIntrinsics.HighestProjectionAsync(this, session, ct);

    // Inserts are flushed before a following projector/query can observe them.
    // Detach immediately: subsequent set-based writes must not leave stale rows.
    internal async Task InsertAsync<T>(T row, CancellationToken ct) where T : class
    {
        Add(row);
        try { await SaveChangesAsync(ct).ConfigureAwait(true); }
        catch (DbUpdateException error) when (error.InnerException is SqliteException sqlite)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sqlite).Throw();
            throw;
        }
        finally { Entry(row).State = EntityState.Detached; }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountStateRow>().ToTable("account_state").HasKey(x => x.id);
        modelBuilder.Entity<AccountRow>().ToTable("account").HasKey(x => x.id);
        modelBuilder.Entity<ControlAccountRow>().ToTable("control_account").HasKey(x => new { x.email, x.url }).HasName("control_account_pk");
        modelBuilder.Entity<CredentialRow>().ToTable("credential").HasKey(x => x.id);
        modelBuilder.Entity<EventSequenceRow>().ToTable("event_sequence").HasKey(x => x.aggregate_id);
        modelBuilder.Entity<EventRow>().ToTable("event").HasKey(x => x.id);
        modelBuilder.Entity<KvRow>().ToTable("kv").HasKey(x => x.key);
        modelBuilder.Entity<PermissionRow>().ToTable("permission").HasKey(x => x.id);
        modelBuilder.Entity<ProjectDirectoryRow>().ToTable("project_directory").HasKey(x => new { x.project_id, x.directory }).HasName("project_directory_pk");
        modelBuilder.Entity<ProjectRow>().ToTable("project").HasKey(x => x.id);
        modelBuilder.Entity<InstructionBlobRow>().ToTable("instruction_blob").HasKey(x => x.hash);
        modelBuilder.Entity<InstructionEntryRow>().ToTable("instruction_entry").HasKey(x => new { x.session_id, x.key }).HasName("instruction_entry_pk");
        modelBuilder.Entity<InstructionStateRow>().ToTable("instruction_state").HasKey(x => x.session_id);
        modelBuilder.Entity<InboxRow>().ToTable("session_inbox").HasKey(x => x.id);
        modelBuilder.Entity<MessageRow>().ToTable("session_message").HasKey(x => x.id);
        modelBuilder.Entity<PendingRow>().ToTable("session_pending").HasKey(x => x.id);
        modelBuilder.Entity<SessionRow>().ToTable("session_v2").HasKey(x => x.id);
        modelBuilder.Entity<WorkspaceRow>().ToTable("workspace").HasKey(x => x.id);
        modelBuilder.Entity<WorktreeRow>().ToTable("worktree").HasKey(x => new { x.project_id, x.directory }).HasName("worktree_pk");

        modelBuilder.Entity<AccountStateRow>().HasOne<AccountRow>().WithMany().HasForeignKey(x => x.active_account_id).OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_account_state_active_account_id_account_id_fk");
        modelBuilder.Entity<EventRow>().HasOne<EventSequenceRow>().WithMany().HasForeignKey(x => x.aggregate_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_event_aggregate_id_event_sequence_aggregate_id_fk");
        modelBuilder.Entity<PermissionRow>().HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.project_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_permission_project_id_project_id_fk");
        modelBuilder.Entity<ProjectDirectoryRow>().HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.project_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_project_directory_project_id_project_id_fk");
        modelBuilder.Entity<WorktreeRow>().HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.project_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_worktree_project_id_project_id_fk");
        modelBuilder.Entity<SessionRow>().HasOne<ProjectRow>().WithMany().HasForeignKey(x => x.project_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_session_v2_project_id_project_id_fk");
        modelBuilder.Entity<InstructionEntryRow>().HasOne<SessionRow>().WithMany().HasForeignKey(x => x.session_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_instruction_entry_session_id_session_v2_id_fk");
        modelBuilder.Entity<InstructionStateRow>().HasOne<SessionRow>().WithMany().HasForeignKey(x => x.session_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_instruction_state_session_id_session_v2_id_fk");
        modelBuilder.Entity<InboxRow>().HasOne<SessionRow>().WithMany().HasForeignKey(x => x.session_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_session_inbox_session_id_session_v2_id_fk");
        modelBuilder.Entity<MessageRow>().HasOne<SessionRow>().WithMany().HasForeignKey(x => x.session_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_session_message_session_id_session_v2_id_fk");
        modelBuilder.Entity<PendingRow>().HasOne<SessionRow>().WithMany().HasForeignKey(x => x.session_id).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_session_pending_session_id_session_v2_id_fk");

        modelBuilder.Entity<EventRow>().HasIndex(x => new { x.aggregate_id, x.seq }).IsUnique().HasDatabaseName("event_aggregate_seq_idx");
        modelBuilder.Entity<EventRow>().HasIndex(x => new { x.aggregate_id, x.type, x.seq }).HasDatabaseName("event_aggregate_type_seq_idx");
        modelBuilder.Entity<PermissionRow>().HasIndex(x => new { x.project_id, x.action, x.resource }).IsUnique().HasDatabaseName("permission_project_action_resource_idx");
        modelBuilder.Entity<InboxRow>().HasIndex(x => new { x.session_id, x.delivery, x.enqueued_seq }).HasDatabaseName("session_inbox_session_delivery_seq_idx");
        modelBuilder.Entity<InboxRow>().HasIndex(x => new { x.session_id, x.enqueued_seq }).IsUnique().HasDatabaseName("session_inbox_session_enqueued_seq_idx");
        modelBuilder.Entity<MessageRow>().HasIndex(x => new { x.session_id, x.seq }).IsUnique().HasDatabaseName("session_message_session_seq_idx");
        modelBuilder.Entity<MessageRow>().HasIndex(x => new { x.session_id, x.type, x.seq }).HasDatabaseName("session_message_session_type_seq_idx");
        modelBuilder.Entity<MessageRow>().HasIndex(x => new { x.session_id, x.time_created, x.id }).HasDatabaseName("session_message_session_time_created_id_idx");
        modelBuilder.Entity<MessageRow>().HasIndex(x => x.time_created).HasDatabaseName("session_message_time_created_idx");
        modelBuilder.Entity<PendingRow>().HasIndex(x => new { x.session_id, x.delivery, x.admitted_seq }).HasDatabaseName("session_pending_session_delivery_seq_idx");
        modelBuilder.Entity<PendingRow>().HasIndex(x => x.session_id).IsUnique().HasDatabaseName("session_pending_session_compaction_idx").HasFilter("\"session_pending\".\"type\" = 'compaction'");
        modelBuilder.Entity<PendingRow>().HasIndex(x => new { x.session_id, x.admitted_seq }).IsUnique().HasDatabaseName("session_pending_session_admitted_seq_idx");
        modelBuilder.Entity<SessionRow>().HasIndex(x => x.project_id).HasDatabaseName("session_v2_project_idx");
        modelBuilder.Entity<SessionRow>().HasIndex(x => x.workspace_id).HasDatabaseName("session_v2_workspace_idx");
        modelBuilder.Entity<SessionRow>().HasIndex(x => x.parent_id).HasDatabaseName("session_v2_parent_idx");
        modelBuilder.Entity<SessionRow>().HasIndex(x => x.time_suspended).HasDatabaseName("session_v2_time_suspended_idx").HasFilter("\"session_v2\".\"time_suspended\" is not null");

        modelBuilder.Entity<EventRow>().Property(x => x.created).HasDefaultValueSql("0");
        modelBuilder.Entity<InstructionEntryRow>().Property(x => x.removed).HasDefaultValueSql("false");
        foreach (var name in new[] { "cost", "tokens_input", "tokens_output", "tokens_reasoning", "tokens_cache_read", "tokens_cache_write", "resume_attempts" })
            modelBuilder.Entity<SessionRow>().Property(name).HasDefaultValueSql("0");
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(property.Name);
                property.SetColumnType(property.ClrType == typeof(string) ? "TEXT" : property.Name == "cost" ? "REAL" : "INTEGER");
                property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
        modelBuilder.HasDbFunction(typeof(SqliteFunctions).GetMethod(nameof(SqliteFunctions.JsonText))!).HasName("json_extract").IsBuiltIn();
        modelBuilder.HasDbFunction(typeof(SqliteFunctions).GetMethod(nameof(SqliteFunctions.JsonNumber))!).HasName("json_extract").IsBuiltIn();
        modelBuilder.HasDbFunction(typeof(SqliteFunctions).GetMethod(nameof(SqliteFunctions.JsonValid))!).HasName("json_valid").IsBuiltIn();
        // C# long/double comparisons otherwise promote the column to double and
        // EF emits CAST(column AS REAL). Keep SQLite's native INTEGER/REAL
        // comparison, with independently mapped operands and no loss of Int64 bits.
        modelBuilder.HasDbFunction(typeof(SqliteFunctions).GetMethod(nameof(SqliteFunctions.After))!)
            .HasTranslation(args => new SqlBinaryExpression(ExpressionType.GreaterThan, args[0], args[1], typeof(bool), null));
        modelBuilder.HasDbFunction(typeof(SqliteFunctions).GetMethod(nameof(SqliteFunctions.Before))!)
            .HasTranslation(args => new SqlBinaryExpression(ExpressionType.LessThan, args[0], args[1], typeof(bool), null));
        modelBuilder.HasDbFunction(typeof(SqliteFunctions).GetMethod(nameof(SqliteFunctions.AtOrAfter))!)
            .HasTranslation(args => new SqlBinaryExpression(ExpressionType.GreaterThanOrEqual, args[0], args[1], typeof(bool), null));
        modelBuilder.HasDbFunction(typeof(SqliteFunctions).GetMethod(nameof(SqliteFunctions.At))!)
            .HasTranslation(args => new SqlBinaryExpression(ExpressionType.Equal, args[0], args[1], typeof(bool), null));
    }
}

internal static class SqliteFunctions
{
    public static string? JsonText(string json, string path) => throw new InvalidOperationException("SQL-only JSON function.");
    public static double? JsonNumber(string json, string path) => throw new InvalidOperationException("SQL-only JSON function.");
    public static bool JsonValid(string json) => throw new InvalidOperationException("SQL-only JSON function.");
    public static bool After(long value, double boundary) => throw new InvalidOperationException("SQL-only numeric comparison.");
    public static bool Before(long value, double boundary) => throw new InvalidOperationException("SQL-only numeric comparison.");
    public static bool AtOrAfter(long value, double boundary) => throw new InvalidOperationException("SQL-only numeric comparison.");
    public static bool At(long value, double boundary) => throw new InvalidOperationException("SQL-only numeric comparison.");
}

internal static class PersistenceQuery
{
    // SQLite treats a negative LIMIT as unbounded. Queryable.Take only accepts
    // Int32; stream the exceptional larger limit without narrowing it.
    internal static async IAsyncEnumerable<T> ReadAsync<T>(this IQueryable<T> query, long limit, [EnumeratorCancellation] CancellationToken ct)
    {
        if (limit is >= 0 and <= int.MaxValue) query = query.Take((int)limit);
        long read = 0;
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(true))
        {
            // Decode at the caller before reading the next row, matching the
            // source reader's failure/cancellation order without buffering JSON.
            yield return row;
            if (limit >= 0 && ++read >= limit) yield break;
        }
    }
}
