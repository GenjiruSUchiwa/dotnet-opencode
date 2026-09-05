namespace OpenCode.Core.Pty;

using System.Collections;
using System.Text;
using OpenCode.Schema;

public sealed class PtyNotFoundException(PtyId id) : Exception($"PTY session not found: {id}")
{
    public PtyId PtyId { get; } = id;
}

public sealed class PtyExitedException(PtyId id) : Exception($"PTY session exited: {id}");
public sealed record PtyOutput(ReadOnlyMemory<byte> Bytes, string Text, long Cursor);
public sealed record PtyEnd(double? ExitCode = null, Exception? Error = null);
public sealed record PtyChange(string Type, PtyInfo Info);

/// <summary>One instance per local Location lifetime, not per HTTP request or Session.</summary>
public sealed class PtyService(string directory, Func<string> resolveShell) : IAsyncDisposable
{
    private const int BufferLimit = 2 * 1024 * 1024;
    private const int ExitedLimit = 25;
    private readonly Lock gate = new();
    private readonly Dictionary<PtyId, Terminal> terminals = [];
    private readonly Queue<PtyId> exited = [];
    private bool disposed;

    // Subscribers must be nonblocking. The server bridge should enqueue into its event bus.
    public event Action<PtyChange>? Changed;
    public string Directory { get; } = Path.GetFullPath(directory);

    public IReadOnlyList<PtyInfo> List()
    {
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return terminals.Values.Select(x => x.Info).ToArray(); }
    }

    public PtyInfo Get(PtyId id) { lock (gate) return Require(id).Info; }

    public PtyInfo Create(PtyCreateInput input)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var command = string.IsNullOrEmpty(input.Command) ? resolveShell() : input.Command;
            var args = (input.Args ?? []).ToList();
            if (Path.GetFileNameWithoutExtension(command).ToLowerInvariant() is "bash" or "dash" or "fish" or "ksh" or "sh" or "zsh") args.Add("-l");
            var cwd = string.IsNullOrEmpty(input.Cwd) ? Directory : Path.GetFullPath(input.Cwd, Directory);
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) environment[(string)entry.Key] = (string)entry.Value!;
            foreach (var entry in input.Env ?? new Dictionary<string, string>())
            {
                if (entry.Key.Length == 0 || entry.Key.Contains('=') || entry.Key.Contains('\0') || entry.Value.Contains('\0'))
                    throw new ArgumentException("Invalid PTY environment entry.", nameof(input));
                environment[entry.Key] = entry.Value;
            }
            environment["TERM"] = "xterm-256color";
            environment["OPENCODE_TERMINAL"] = "1";
            environment["LC_ALL"] = environment["LC_CTYPE"] = environment["LANG"] = "C.UTF-8";
            var id = PtyId.Ascending();
            var process = WindowsPty.Start(command, args, cwd, environment);
            var info = new PtyInfo(id, string.IsNullOrEmpty(input.Title) ? $"Terminal {id.Value[^4..]}" : input.Title,
                command, args.ToArray(), cwd, PtyStatus.Running, process.Pid);
            var terminal = new Terminal(info, process);
            terminals.Add(id, terminal);
            Publish("pty.created", info);
            terminal.Pump = Pump(terminal);
            return info;
        }
    }

    public PtyInfo Update(PtyId id, PtyUpdateInput input)
    {
        lock (gate)
        {
            var terminal = Require(id);
            if (input.Size is { } size && terminal.Info.Status == PtyStatus.Running && !terminal.Process.Completion.IsCompleted)
            {
                if (size.Cols > short.MaxValue || size.Rows > short.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(input), "ConPTY dimensions cannot exceed 32767.");
                terminal.Process.Resize(checked((short)size.Cols), checked((short)size.Rows));
            }
            if (!string.IsNullOrEmpty(input.Title)) terminal.Info = terminal.Info with { Title = input.Title };
            Publish("pty.updated", terminal.Info);
            return terminal.Info;
        }
    }

    public ValueTask WriteAsync(PtyId id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var terminal = Require(id);
            return terminal.Info.Status == PtyStatus.Running ? terminal.Process.WriteAsync(data, cancellationToken) : ValueTask.CompletedTask;
        }
    }

    public ValueTask WriteAsync(PtyId id, string data, CancellationToken cancellationToken = default) => WriteAsync(id, Encoding.UTF8.GetBytes(data), cancellationToken);

    public async ValueTask RemoveAsync(PtyId id)
    {
        Terminal terminal;
        lock (gate)
        {
            terminal = Require(id);
            terminals.Remove(id);
            End(terminal, new());
            Publish("pty.deleted", terminal.Info);
        }
        await terminal.Process.DisposeAsync().ConfigureAwait(true);
        await terminal.Pump.ConfigureAwait(true);
    }

    public PtyAttachment Attach(PtyId id, Action<PtyOutput> onData, Action<PtyEnd> onEnd, long? cursor = null)
    {
        lock (gate)
        {
            var terminal = Require(id);
            if (terminal.Info.Status != PtyStatus.Running) throw new PtyExitedException(id);
            var from = cursor == -1 ? terminal.Cursor : Math.Max(0, cursor ?? 0);
            var offset = Math.Clamp(from - (terminal.Cursor - terminal.Buffer.Length), 0, terminal.Buffer.Length);
            var subscriber = new Subscriber(onData, onEnd);
            terminal.Subscribers.Add(subscriber);
            return new PtyAttachment(terminal.Buffer[(int)offset..], terminal.Cursor,
                (data, cancellationToken) => terminal.Info.Status == PtyStatus.Running ? terminal.Process.WriteAsync(data, cancellationToken) : ValueTask.CompletedTask,
                () =>
                {
                    lock (gate)
                    {
                        if (subscriber.Active || subscriber.Detached) return;
                        subscriber.Active = true;
                        foreach (var chunk in subscriber.Pending) Deliver(terminal, subscriber, chunk);
                        subscriber.Pending.Clear();
                        if (subscriber.End is { } end) Notify(subscriber, end);
                    }
                },
                () =>
                {
                    lock (gate)
                    {
                        subscriber.Detached = true;
                        subscriber.Pending.Clear();
                        subscriber.End = null;
                        terminal.Subscribers.Remove(subscriber);
                    }
                });
        }
    }

    private async Task Pump(Terminal terminal)
    {
        Exception? failure = null;
        double? code = null;
        try
        {
            var decoder = Encoding.UTF8.GetDecoder();
            await foreach (var bytes in terminal.Process.Output.ReadAllAsync().ConfigureAwait(true))
            {
                var text = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                var count = decoder.GetChars(bytes, text, false);
                Output(terminal, new(bytes, new string(text, 0, count), 0));
            }
            var final = new char[2];
            var remaining = decoder.GetChars([], final, true);
            if (remaining > 0) Output(terminal, new(ReadOnlyMemory<byte>.Empty, new string(final, 0, remaining), 0));
            code = await terminal.Process.Completion.ConfigureAwait(true);
        }
        catch (Exception error) { failure = error; }
        finally { await terminal.Process.DisposeAsync().ConfigureAwait(true); }
        var removals = new List<PtyId>();
        lock (gate)
        {
            if (!terminals.ContainsKey(terminal.Info.Id)) return;
            terminal.Info = terminal.Info with { Status = PtyStatus.Exited, ExitCode = code };
            End(terminal, new(code, failure));
            Publish("pty.exited", terminal.Info);
            exited.Enqueue(terminal.Info.Id);
            // Removed entries do not consume the retention allowance.
            while (exited.Count > 0 && !terminals.ContainsKey(exited.Peek())) exited.Dequeue();
            while (exited.Count(id => terminals.ContainsKey(id)) > ExitedLimit)
            {
                var id = exited.Dequeue();
                if (terminals.ContainsKey(id)) removals.Add(id);
            }
        }
        foreach (var id in removals)
        {
            lock (gate)
            {
                if (!terminals.Remove(id, out var removed)) continue;
                End(removed, new());
                Publish("pty.deleted", removed.Info);
            }
        }
    }

    private void Output(Terminal terminal, PtyOutput output)
    {
        lock (gate)
        {
            if (!terminals.ContainsKey(terminal.Info.Id)) return;
            terminal.Cursor += output.Text.Length;
            output = output with { Cursor = terminal.Cursor };
            terminal.Buffer += output.Text;
            if (terminal.Buffer.Length > BufferLimit) terminal.Buffer = terminal.Buffer[^BufferLimit..];
            foreach (var subscriber in terminal.Subscribers.ToArray())
            {
                if (!subscriber.Active) subscriber.Pending.Add(output);
                else Deliver(terminal, subscriber, output);
            }
        }
    }

    private static void Deliver(Terminal terminal, Subscriber subscriber, PtyOutput output)
    {
        if (subscriber.Detached) return;
        try { subscriber.OnData(output); }
        catch { subscriber.Detached = true; terminal.Subscribers.Remove(subscriber); }
    }

    private static void Notify(Subscriber subscriber, PtyEnd end)
    {
        if (subscriber.Detached) return;
        try { subscriber.OnEnd(end); }
        catch { /* Transport failure must not stop PTY cleanup. */ }
    }

    private static void End(Terminal terminal, PtyEnd end)
    {
        foreach (var subscriber in terminal.Subscribers.ToArray())
        {
            if (subscriber.Active) Notify(subscriber, end);
            else subscriber.End = end;
        }
        terminal.Subscribers.Clear();
    }

    private void Publish(string type, PtyInfo info)
    {
        foreach (var callback in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<PtyChange>)callback)(new(type, info)); }
            catch { /* A failed event consumer must not leak a child process. */ }
        }
    }

    private Terminal Require(PtyId id)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return terminals.TryGetValue(id, out var terminal) ? terminal : throw new PtyNotFoundException(id);
    }

    public async ValueTask DisposeAsync()
    {
        Terminal[] owned;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            owned = terminals.Values.ToArray();
            foreach (var terminal in owned) End(terminal, new());
            terminals.Clear();
            exited.Clear();
        }
        foreach (var terminal in owned) await terminal.Process.DisposeAsync().ConfigureAwait(true);
        await Task.WhenAll(owned.Select(x => x.Pump)).ConfigureAwait(true);
    }

    private sealed class Terminal(PtyInfo info, WindowsPty process)
    {
        internal PtyInfo Info = info;
        internal readonly WindowsPty Process = process;
        internal string Buffer = "";
        internal long Cursor;
        internal readonly List<Subscriber> Subscribers = [];
        internal Task Pump = Task.CompletedTask;
    }

    private sealed class Subscriber(Action<PtyOutput> onData, Action<PtyEnd> onEnd)
    {
        internal readonly Action<PtyOutput> OnData = onData;
        internal readonly Action<PtyEnd> OnEnd = onEnd;
        internal readonly List<PtyOutput> Pending = [];
        internal bool Active, Detached;
        internal PtyEnd? End;
    }
}

public sealed class PtyAttachment(string replay, long cursor,
    Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, Action activate, Action detach) : IDisposable
{
    public string Replay { get; } = replay;
    // Basic PTY cursors count UTF-16 code units, NOT bytes (unlike persistent PTY offsets).
    public long Cursor { get; } = cursor;
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => write(data, cancellationToken);
    public void Activate() => activate();
    public void Dispose() => detach();
}
