namespace OpenCode.Core.Session;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Core.Database;
using OpenCode.Core.Event;
using OpenCode.Core.Instructions;
using OpenCode.Schema;

public sealed class InstructionEntryValueTooLargeException(int actualBytes)
    : ArgumentException($"Instruction entry value is {actualBytes} bytes; the limit is {SessionInstructionEntries.MaxValueBytes} bytes")
{
    public int ActualBytes { get; } = actualBytes;
    public int MaxBytes => SessionInstructionEntries.MaxValueBytes;
}

/// <summary>Session-owned context producers. Mutations take effect at the next instruction boundary;
/// they do not rewrite the epoch baseline, append a message, or wake execution.</summary>
public sealed class SessionInstructionEntries(IDatabase database)
{
    public const int MaxValueBytes = 8 * 1024;

    public async Task<IReadOnlyList<InstructionEntryInfo>> ListAsync(SessionId sessionId, CancellationToken ct = default) =>
        (await new InstructionPersistence(database).EntriesAsync(sessionId, ct).ConfigureAwait(false))
            .Where(entry => !entry.Removed).Select(entry => new InstructionEntryInfo(entry.Key, entry.Value)).ToArray();

    public Task PutAsync(SessionId sessionId, string key, JsonElement value, CancellationToken ct = default)
    {
        RequireKey(key);
        // Use JSON.stringify semantics, not raw input bytes, pretty JSON, escaped ASCII,
        // or sorted hashing: the source compares the stored JSON representation.
        var json = InstructionJson.Stringify(value);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxValueBytes) throw new InstructionEntryValueTooLargeException(bytes);
        return new InstructionEntryPersistence(database).PutAsync(sessionId, key,
            value.ValueKind == JsonValueKind.Null ? null : json, ct);
    }

    public Task RemoveAsync(SessionId sessionId, string key, CancellationToken ct = default)
    {
        RequireKey(key);
        return new InstructionEntryPersistence(database).RemoveAsync(sessionId, key, ct);
    }

    private static void RequireKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Regex.IsMatch(key, "^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new ArgumentException("Instruction entry key must use lowercase alphanumerics plus . _ - and start with an alphanumeric character.", nameof(key));
    }
}
