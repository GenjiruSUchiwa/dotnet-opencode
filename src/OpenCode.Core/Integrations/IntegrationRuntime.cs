namespace OpenCode.Core.Integrations;

using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Forms;
using OpenCode.Schema;

public sealed record IntegrationDefinition(IntegrationRef Reference, IReadOnlyList<IntegrationMethod> Methods,
    IReadOnlyDictionary<string, Func<FormAnswer?, string?, CancellationToken, Task<IntegrationAuthorization>>> OAuth);

/// <summary>One real provider flow. Completion includes credential persistence and committed notification publication.</summary>
public sealed record IntegrationAuthorization(string Url, string Instructions, string Mode, DateTimeOffset ExpiresAt,
    Func<string?, CancellationToken, Task> CompleteAsync, Func<ValueTask> DisposeAsync);

public sealed class IntegrationAuthorizationException(string message, string kind = "integration_authorization") : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>Location-owned integration catalog and attempts. No process-global attempt lookup or provider I/O in construction.</summary>
public sealed partial class IntegrationRuntime(CredentialStore credentials, Func<CredentialMutation, CancellationToken, Task> publish, Action updated) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IntegrationDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<IntegrationAttemptId, Entry> _attempts = [];
    private readonly CancellationTokenSource _shutdown = new();
    private string? _fingerprint;
    private Task? _scrubber;
    private bool _closed;

    private sealed class Entry(string integration, IntegrationAttempt info, IntegrationAuthorization authorization, CancellationToken shutdown)
    {
        public string Integration { get; } = integration;
        public IntegrationAttempt Info { get; } = info;
        public IntegrationAuthorization Authorization { get; } = authorization;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        public IntegrationAttemptStatus Status = new IntegrationPendingAttemptStatus(info.Time);
        public bool Completing;
        public bool Cancelled;
        public DateTimeOffset? RemoveAt;
        public Task Worker = Task.CompletedTask;
    }

    public void Define(IReadOnlyList<IntegrationDefinition> definitions)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _definitions.Clear();
            foreach (var definition in definitions) _definitions[definition.Reference.Id.Value] = definition;
            var fingerprint = JsonSerializer.Serialize(definitions.Select(item => new IntegrationInfo(item.Reference.Id,
                item.Reference.Name, item.Methods, [], item.Reference.Metadata)));
            if (fingerprint == _fingerprint) return;
            _fingerprint = fingerprint;
        }
        updated();
    }

    public async Task<IReadOnlyList<IntegrationInfo>> ListAsync(CancellationToken ct = default)
    {
        IntegrationDefinition[] definitions;
        lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); definitions = _definitions.Values.ToArray(); }
        var saved = await credentials.ListCredentialsAsync(ct);
        return definitions.Select(item => Project(item, saved.Where(value => value.IntegrationId == item.Reference.Id.Value)))
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<IntegrationInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        IntegrationDefinition? definition;
        lock (_gate) { ObjectDisposedException.ThrowIf(_closed, this); definition = _definitions.GetValueOrDefault(id); }
        return definition is null ? null : Project(definition, await credentials.ListCredentialsForIntegrationAsync(id, ct));
    }

    public async Task ConnectKeyAsync(string id, IntegrationKeyConnectPayload input, CancellationToken ct = default)
    {
        var definition = RequireDefinition(id);
        var method = definition.Methods.OfType<IntegrationKeyMethod>().FirstOrDefault()
            ?? throw new IntegrationAuthorizationException("This integration has no key method.");
        ValidateAnswer(method.Form, input.Answer, allowUnknown: false);
        var value = new CredentialKey(input.Key, Configuration: input.Answer is { Count: > 0 } ? input.Answer : null);
        var label = input.Label ?? await UniqueLabelAsync(credentials, id, definition.Reference.Name, ct);
        var mutation = await credentials.CreateAsync(id, JsonSerializer.SerializeToElement(value, OpenCodeJsonContext.Default.CredentialValue), label, ct);
        await publish(mutation, CancellationToken.None);
    }

    /// <summary>Credential methods are channel-global, as in Integration.connection. Missing IDs are source no-ops.</summary>
    public async Task ActivateCredentialAsync(CredentialId id, CancellationToken ct = default) =>
        await publish(await credentials.ActivateAsync(id.Value, ct), CancellationToken.None);

    public async Task UpdateCredentialAsync(CredentialId id, string label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        // The wire contract accepts any string, including empty. Interactive rename UI applies its
        // own trim/nonempty policy; the public endpoint never accepts a credential value update.
        await publish(await credentials.UpdateAsync(id.Value, label: label, ct: ct), CancellationToken.None);
    }

    public async Task RemoveCredentialAsync(CredentialId id, CancellationToken ct = default) =>
        await publish(await credentials.RemoveAsync(id.Value, ct), CancellationToken.None);

    public async Task<IntegrationAttempt> ConnectOAuthAsync(string id, IntegrationOAuthConnectPayload input, CancellationToken ct = default)
    {
        var definition = RequireDefinition(id);
        var method = definition.Methods.OfType<IntegrationOAuthMethod>().FirstOrDefault(item => item.Id == input.MethodId.Value);
        if (method is null || !definition.OAuth.TryGetValue(input.MethodId.Value, out var begin))
            throw new IntegrationAuthorizationException("OAuth method not found.");
        ValidateAnswer(method.Form, input.Answer, allowUnknown: true);
        using var setup = credentials.Clock.CreateLinkedCancellationTokenSource(ct, _shutdown.Token);
        setup.CancelAfter(TimeSpan.FromMinutes(10));
        var authorization = await begin(input.Answer, input.Label, setup.Token);
        try
        {
            setup.Token.ThrowIfCancellationRequested();
            var now = credentials.Clock.GetUtcNow();
            if (authorization.ExpiresAt <= now) throw new IntegrationAuthorizationException("Authorization expired before it could start.");
            var info = new IntegrationAttempt(IntegrationAttemptId.Create(), authorization.Url, authorization.Instructions,
                authorization.Mode, new(now.ToUnixTimeMilliseconds(), authorization.ExpiresAt.ToUnixTimeMilliseconds()));
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                var entry = new Entry(id, info, authorization, _shutdown.Token);
                _attempts.Add(info.AttemptId, entry);
                _scrubber ??= ScrubAsync();
                if (authorization.Mode == "auto")
                {
                    entry.Completing = true;
                    entry.Worker = RunAsync(entry, null);
                }
            }
            return info;
        }
        catch { await authorization.DisposeAsync(); throw; }
    }

    public IntegrationAttemptStatus Status(string integration, IntegrationAttemptId attempt)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return RequireAttempt(integration, attempt).Status;
        }
    }

    public async Task CompleteAsync(string integration, IntegrationAttemptId attempt, string? code, CancellationToken ct = default)
    {
        Task worker;
        lock (_gate)
        {
            var entry = RequireAttempt(integration, attempt);
            if (entry.Status is not IntegrationPendingAttemptStatus) return;
            if (entry.Authorization.Mode == "code" && code is null)
                throw new IntegrationAuthorizationException("Authorization code is required", "integration_code_required");
            if (entry.Completing) throw new IntegrationAuthorizationException("OAuth attempt is already completing.");
            entry.Completing = true;
            worker = entry.Worker = RunAsync(entry, code);
        }
        await worker.WaitAsync(ct);
        if (Status(integration, attempt) is IntegrationFailedAttemptStatus) throw new IntegrationAuthorizationException("Authentication failed");
    }

    public async Task CancelAsync(string integration, IntegrationAttemptId attempt)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_attempts.TryGetValue(attempt, out entry) || entry.Integration != integration || entry.Status is not IntegrationPendingAttemptStatus || entry.Cancelled) return;
            entry.Cancelled = true;
        }
        await entry.Cancellation.CancelAsync();
        if (!entry.Completing) await entry.Authorization.DisposeAsync();
        await entry.Worker;
        // Existing provider APIs include persistence. If they returned a committed success despite
        // cancellation, retain complete rather than report that an existing credential was cancelled.
        lock (_gate)
        {
            if (entry.Status is IntegrationCompleteAttemptStatus) return;
            _attempts.Remove(attempt);
            entry.Cancellation.Dispose();
        }
    }

    private async Task RunAsync(Entry entry, string? code)
    {
        // Begin on another continuation so provider work cannot run under the registry lock.
        await Task.Yield();
        try
        {
            await entry.Authorization.CompleteAsync(code, entry.Cancellation.Token);
            lock (_gate) entry.Status = new IntegrationCompleteAttemptStatus(entry.Info.Time);
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (credentials.Clock.GetUtcNow().ToUnixTimeMilliseconds() >= entry.Info.Time.Expires)
                    entry.Status = new IntegrationExpiredAttemptStatus(entry.Info.Time);
                else entry.Status = new IntegrationFailedAttemptStatus(entry.Info.Time, "Authentication failed");
            }
        }
        finally
        {
            try { await entry.Authorization.DisposeAsync(); }
            catch (Exception error)
            {
                // Persistence outcome is already settled. Do not replace a committed success with
                // a cleanup error or leave an unobserved worker fault holding callback resources.
                System.Diagnostics.Trace.TraceWarning("Integration authorization cleanup failed ({0}).", error.GetType().Name);
            }
            finally { lock (_gate) entry.RemoveAt = credentials.Clock.GetUtcNow().AddMinutes(1); }
        }
    }

    private async Task ScrubAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), credentials.Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                await ScrubCommandsAsync();
                Entry[] expired;
                lock (_gate)
                {
                    var now = credentials.Clock.GetUtcNow();
                    foreach (var id in _attempts.Where(item => item.Value.RemoveAt <= now).Select(item => item.Key).ToArray())
                    {
                        _attempts[id].Cancellation.Dispose();
                        _attempts.Remove(id);
                    }
                    expired = _attempts.Values.Where(entry => entry.Status is IntegrationPendingAttemptStatus && entry.Info.Time.Expires <= now.ToUnixTimeMilliseconds()).ToArray();
                    foreach (var entry in expired) entry.Status = new IntegrationExpiredAttemptStatus(entry.Info.Time);
                }
                foreach (var entry in expired)
                {
                    await entry.Cancellation.CancelAsync();
                    if (entry.Completing) continue;
                    await entry.Authorization.DisposeAsync();
                    lock (_gate) entry.RemoveAt = credentials.Clock.GetUtcNow().AddMinutes(1);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private IntegrationDefinition RequireDefinition(string id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _definitions.GetValueOrDefault(id) ?? throw new IntegrationAuthorizationException("Integration not found.");
        }
    }

    private Entry RequireAttempt(string integration, IntegrationAttemptId attempt) =>
        _attempts.TryGetValue(attempt, out var entry) && entry.Integration == integration ? entry
            : throw new IntegrationAuthorizationException("OAuth attempt not found.");

    private static IntegrationInfo Project(IntegrationDefinition definition, IEnumerable<StoredCredential> saved) =>
        new(definition.Reference.Id, definition.Reference.Name, definition.Methods,
            saved.Reverse().Select(item => (ConnectionInfo)new ConnectionCredentialInfo(CredentialId.FromExisting(item.Id), item.Label)).ToArray(), definition.Reference.Metadata);

    private static void ValidateAnswer(IReadOnlyList<FormField>? fields, FormAnswer? answer, bool allowUnknown)
    {
        if (fields is null)
        {
            if (!allowUnknown && answer is { Count: > 0 }) throw new IntegrationAuthorizationException("Key method does not accept a form answer");
            return;
        }
        var invalid = FormValidation.Fields(fields) ?? FormValidation.Answer(fields, answer ?? new FormAnswer(new Dictionary<string, FormValue>()));
        if (invalid is not null) throw new IntegrationAuthorizationException(invalid);
    }

    public static async Task<string> UniqueLabelAsync(CredentialStore store, string integration, string name, CancellationToken ct)
    {
        var labels = (await store.ListCredentialsForIntegrationAsync(integration, ct)).Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, labels.Count + 1).Select(index => index == 0 ? name : $"{name} {index + 1}").First(label => !labels.Contains(label));
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate) { if (_closed) return; _closed = true; entries = _attempts.Values.ToArray(); }
        await _shutdown.CancelAsync();
        foreach (var entry in entries)
            if (entry.Status is IntegrationPendingAttemptStatus) await entry.Cancellation.CancelAsync();
        foreach (var entry in entries)
        {
            if (!entry.Completing && entry.Status is IntegrationPendingAttemptStatus) await entry.Authorization.DisposeAsync();
            await entry.Worker;
            entry.Cancellation.Dispose();
        }
        if (_scrubber is not null) await _scrubber;
        await DisposeCommandsAsync();
        lock (_gate) { _attempts.Clear(); _definitions.Clear(); }
    }
}
