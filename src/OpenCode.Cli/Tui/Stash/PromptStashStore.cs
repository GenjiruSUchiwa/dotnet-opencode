namespace OpenCode.Cli.Tui.Stash;

using System.Text;
using OpenCode.Schema;

public enum StashWriteOutcome { NoChange, Saved, Failed }
public sealed record StashWriteResult(StashWriteOutcome Outcome, long Revision, string? Error = null);
public sealed record StashMutation(StashEntry? Entry, Task<StashWriteResult> Persistence);
public sealed record StashSnapshot(IReadOnlyList<StashEntry> Entries, bool Loaded, bool Dirty, long Revision, string? Error);

/// <summary>Root-owned, memory-first source stash with serialized best-effort JSONL persistence. Construction does no I/O.</summary>
public sealed class PromptStashStore
{
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private StashSnapshot _state = new(Array.Empty<StashEntry>(), false, false, 0, null);
    private Task<StashWriteResult>? _load;
    private Task<StashWriteResult> _pending = Task.FromResult(new StashWriteResult(StashWriteOutcome.NoChange, 0));
    private bool _safeToWrite;
    private bool _writeFailed;
    private long _savedRevision;
    public string FilePath { get; }
    public event Action? Changed;
    public StashSnapshot Snapshot { get { lock (_gate) return _state; } }

    public PromptStashStore(string? stateHome = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var root = stateHome ?? (Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } configured
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"));
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Client state home must be absolute.", nameof(stateHome));
        // Same opencode/<channel>/tui convention as SessionUiStorage; never read/import the TS stash.
        FilePath = Path.Combine(root, "opencode", OpenCodeChannel.Name, "tui", "prompt-stash.jsonl");
    }

    public Task<StashWriteResult> LoadAsync(CancellationToken ct = default)
    {
        lock (_gate) return (_load ??= LoadCoreAsync()).WaitAsync(ct);
    }

    private async Task<StashWriteResult> LoadCoreAsync()
    {
        await Task.Yield();
        string text;
        try { text = await File.ReadAllTextAsync(FilePath, Encoding.UTF8); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { text = ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _state = _state with { Loaded = true, Error = "Could not read the stash. Changes will remain in memory; the unread file will not be overwritten." };
            Notify();
            return new(StashWriteOutcome.Failed, 0, Snapshot.Error);
        }
        Task<StashWriteResult> write;
        lock (_gate)
        {
            var entries = PromptStashCodec.Parse(text);
            _safeToWrite = true;
            _state = new(entries, true, entries.Count > 0, 0, null);
            // Source rewrites valid retained entries on load, but does not erase an all-invalid file.
            write = entries.Count > 0 ? QueueWrite(null, rewrite: true) : Task.FromResult(new StashWriteResult(StashWriteOutcome.NoChange, 0));
        }
        Notify();
        return await write;
    }

    public IReadOnlyList<StashEntry> List() => Snapshot.Entries;

    /// <summary>Memory acceptance is synchronous. Root may clear its captured draft immediately, then observe Persistence separately.</summary>
    public StashMutation Push(StashPrompt prompt, PromptStashAccess access)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        access.RequireEditable();
        if (prompt.HasAdmissionIdentity) throw new InvalidOperationException("A bound admission cannot be stashed as a new prompt.");
        StashMutation result;
        lock (_gate)
        {
            RequireLoaded();
            var entry = new StashEntry(StashPrompt.ParsePromptInfo(prompt.Value)!, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            var trimmed = _state.Entries.Count >= PromptStashCodec.MaximumEntries;
            _state = _state with { Entries = Array.AsReadOnly(_state.Entries.Append(entry).TakeLast(PromptStashCodec.MaximumEntries).ToArray()), Dirty = true, Revision = _state.Revision + 1 };
            result = new(entry, QueueWrite(entry, trimmed));
        }
        Notify();
        return result;
    }

    public StashMutation Pop(PromptStashAccess access)
    {
        access.RequireEditable();
        StashMutation result;
        lock (_gate)
        {
            RequireLoaded();
            result = _state.Entries.Count == 0 ? NoChange() : TakeLocked(_state.Entries.Count - 1, _state.Entries[^1]);
        }
        if (result.Entry is not null) Notify();
        return result;
    }

    /// <summary>The UI supplies the entry it actually displayed so shifted indexes cannot restore a different draft.</summary>
    public StashMutation Take(int index, StashEntry expected, PromptStashAccess access)
    {
        access.RequireEditable();
        StashMutation result;
        lock (_gate)
        {
            RequireLoaded();
            if (index < 0 || index >= _state.Entries.Count || !ReferenceEquals(_state.Entries[index], expected))
                throw new InvalidOperationException("The stash changed. Select the entry again.");
            result = TakeLocked(index, expected);
        }
        Notify();
        return result;
    }

    public StashMutation Remove(int index, StashEntry? expected = null)
    {
        StashMutation result;
        lock (_gate)
        {
            RequireLoaded();
            if (index < 0 || index >= _state.Entries.Count) return NoChange();
            if (expected is not null && !ReferenceEquals(expected, _state.Entries[index]))
                throw new InvalidOperationException("The stash changed. Confirm the entry again.");
            result = RemoveLocked(index);
        }
        Notify();
        return result;
    }

    private StashMutation TakeLocked(int index, StashEntry entry)
    {
        if (entry.Prompt.HasAdmissionIdentity) throw new InvalidOperationException("This entry retains an admission identity and cannot be restored as a new request.");
        return RemoveLocked(index);
    }

    private StashMutation RemoveLocked(int index)
    {
        var entry = _state.Entries[index];
        _state = _state with { Entries = Array.AsReadOnly(_state.Entries.Where((_, position) => position != index).ToArray()), Dirty = true, Revision = _state.Revision + 1 };
        return new(entry, QueueWrite(null, rewrite: true));
    }

    public Task<StashWriteResult> FlushAsync() { lock (_gate) return _pending; }

    public Task<StashWriteResult> RetrySaveAsync()
    {
        lock (_gate)
        {
            RequireLoaded();
            return _state.Dirty ? QueueWrite(null, rewrite: true) : _pending;
        }
    }

    private StashMutation NoChange() => new(null, Task.FromResult(new StashWriteResult(StashWriteOutcome.NoChange, _state.Revision)));
    private void RequireLoaded() { if (!_state.Loaded) throw new InvalidOperationException("Load the stash before accepting or consuming drafts."); }

    private Task<StashWriteResult> QueueWrite(StashEntry? appended, bool rewrite)
    {
        var snapshot = _state;
        return _pending = PersistAsync(_pending, snapshot, appended, rewrite);
    }

    private async Task<StashWriteResult> PersistAsync(Task<StashWriteResult> previous, StashSnapshot snapshot, StashEntry? appended, bool rewrite)
    {
        await Task.Yield();
        await previous;
        bool append;
        lock (_gate)
        {
            if (!_safeToWrite) return new(StashWriteOutcome.Failed, snapshot.Revision, _state.Error);
            // After a failed write, rewrite the complete accepted memory snapshot instead of
            // appending only the newest item and claiming older unsaved entries were persisted.
            append = !rewrite && !_writeFailed && appended is not null && _savedRevision == snapshot.Revision - 1;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (append) await File.AppendAllTextAsync(FilePath, PromptStashCodec.Encode(appended!) + "\n", new UTF8Encoding(false));
            else
            {
                var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, PromptStashCodec.EncodeLines(snapshot.Entries), new UTF8Encoding(false));
                    File.Move(temporary, FilePath, overwrite: true);
                }
                finally
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
            lock (_gate)
            {
                _savedRevision = snapshot.Revision;
                _writeFailed = false;
                _state = _state with { Dirty = _savedRevision < _state.Revision, Error = null };
            }
            Notify();
            return new(StashWriteOutcome.Saved, snapshot.Revision);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            const string message = "Stash changed in memory, but saving prompt-stash.jsonl failed.";
            lock (_gate) { _writeFailed = true; _state = _state with { Dirty = true, Error = message }; }
            Notify();
            return new(StashWriteOutcome.Failed, snapshot.Revision, message);
        }
    }

    private void Notify()
    {
        if (Changed is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
        {
            try { observer(); }
            catch (Exception) { System.Diagnostics.Trace.TraceWarning("A stash observer failed."); }
        }
    }
}
