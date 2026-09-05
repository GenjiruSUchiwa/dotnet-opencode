namespace OpenTui.Native;

using System.Buffers.Binary;
using System.Text;

/// <summary>Lazy native host clipboard owner. Construction does not read, create a worker, or load native code.</summary>
public sealed class NativeClipboardService : IAsyncDisposable
{
    private readonly TimeProvider _clock;
    private enum Kind { Read, Write, Clear }
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Task<NativeClipboardResult>> _active = [];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private uint _service;
    private bool _disposing;
    private bool _providerActive;
    private bool _providerRunning;
    private Task _providerTask = Task.CompletedTask;
    private Task? _disposeTask;
    public NativeClipboardOptions Options { get; }

    public NativeClipboardService(NativeClipboardOptions? options = null, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Options = options ?? new();
        if (Options.MaxConcurrentOperations == 0 || Options.MaxProviderTransfers == 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (Options.WaylandSeat is { } seat && (seat.Length == 0 || seat.Contains('\0'))) throw new ArgumentException("Wayland seat must be nonempty and contain no NUL.", nameof(options));
    }

    public Task<NativeClipboardResult> ReadAsync(IReadOnlyList<string> preferredTypes, NativeClipboardSelection selection = NativeClipboardSelection.Clipboard,
        CancellationToken cancellationToken = default) => Start(Kind.Read, EncodeRequest(preferredTypes), selection, cancellationToken);

    public Task<NativeClipboardResult> WriteTextAsync(string text, NativeClipboardSelection selection = NativeClipboardSelection.Clipboard,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (text.Contains('\0')) throw new ArgumentException("Clipboard text cannot contain NUL.", nameof(text));
        if ((long)Utf8.GetByteCount(text) > Options.MaxWriteBytes) throw new ArgumentOutOfRangeException(nameof(text), "Clipboard write byte limit exceeded.");
        return Start(Kind.Write, Utf8.GetBytes(text), selection, cancellationToken);
    }

    public Task<NativeClipboardResult> ClearAsync(NativeClipboardSelection selection = NativeClipboardSelection.Clipboard,
        CancellationToken cancellationToken = default) => Start(Kind.Clear, [], selection, cancellationToken);

    public static byte[] EncodeRequest(IReadOnlyList<string> preferredTypes)
    {
        ArgumentNullException.ThrowIfNull(preferredTypes);
        if (preferredTypes.Count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(preferredTypes), "Supply 1–64 MIME essence preferences.");
        var types = preferredTypes.Select(type =>
        {
            if (type is null || type.Length is < 3 or > 255) throw new ArgumentException("MIME essences must be at most 255 ASCII bytes.", nameof(preferredTypes));
            var separator = type.IndexOf('/');
            if (separator <= 0 || separator == type.Length - 1 || type.LastIndexOf('/') != separator ||
                type.Where(character => character != '/').Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+.^_`|~-".Contains(character)))
                throw new ArgumentException("MIME preferences require type/subtype essences without parameters.", nameof(preferredTypes));
            return Encoding.ASCII.GetBytes(type.ToLowerInvariant());
        }).ToArray();
        var request = new byte[checked(4 + types.Sum(type => 4 + type.Length))];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)types.Length);
        var offset = 4;
        foreach (var type in types)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(offset), (uint)type.Length);
            type.CopyTo(request, offset + 4);
            offset += 4 + type.Length;
        }
        return request;
    }

    private Task<NativeClipboardResult> Start(Kind kind, byte[] request, NativeClipboardSelection selection, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(selection)) throw new ArgumentOutOfRangeException(nameof(selection));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(new NativeClipboardResult(NativeClipboardOperationStatus.Cancelled));
            if (Options.TimeoutMilliseconds == 0) return Task.FromResult(new NativeClipboardResult(NativeClipboardOperationStatus.TimedOut));
            if (_service == 0)
            {
                _service = OpenTuiNative.CreateClipboardService(Options.MaxConcurrentOperations, Options.MaxProviderTransfers,
                    Options.WaylandSeat is null ? [] : Utf8.GetBytes(Options.WaylandSeat));
                if (_service == 0) throw new InvalidOperationException("Native clipboard service creation failed.");
            }
            uint operation;
            var status = kind switch
            {
                Kind.Read => OpenTuiNative.StartClipboardRead(_service, request, selection, Options, out operation),
                Kind.Write => OpenTuiNative.StartClipboardWrite(_service, request, selection, Options.TimeoutMilliseconds, out operation),
                _ => OpenTuiNative.StartClipboardClear(_service, selection, Options.TimeoutMilliseconds, out operation)
            };
            if (status != NativeClipboardStartStatus.Ok || operation == 0)
                return Task.FromResult(Failed($"Native clipboard operation failed to start ({status})."));
            var task = CompleteOperation(operation, kind, cancellationToken);
            _active.Add(operation, task);
            return task;
        }
    }

    private async Task<NativeClipboardResult> CompleteOperation(uint operation, Kind kind, CancellationToken cancellationToken)
    {
        // Let Start register ownership before completion, including immediate native results.
        await Task.Yield();
        var retired = false;
        NativeClipboardResult? result = null;
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_disposing || cancellationToken.IsCancellationRequested) OpenTuiNative.CancelClipboardOperation(operation);
                    _providerActive |= OpenTuiNative.DrainClipboardService(_service) == 1;
                    var status = OpenTuiNative.PollClipboardOperation(operation);
                    if (status != NativeClipboardOperationStatus.Pending)
                    {
                        result ??= ReadResult(operation, kind, status);
                        var destroyed = OpenTuiNative.DestroyClipboardOperation(operation);
                        if (destroyed != NativeClipboardDestroyStatus.NotReady)
                        {
                            retired = true;
                            _providerActive = true;
                            EnsureProviderPump();
                            return destroyed == NativeClipboardDestroyStatus.Destroyed ? result : Failed("Native clipboard operation became invalid before destruction.");
                        }
                    }
                }
                // Native owns the operation deadline. Cancellation still waits for
                // worker retirement; it never frees a running native operation.
                // Retain the caller context, as the original await did.
                await Task.Delay(TimeSpan.FromMilliseconds(1), _clock, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception exception) { return Failed(exception.Message); }
        finally
        {
            try { if (!retired) await Retire(operation).ConfigureAwait(true); }
            finally { lock (_gate) _active.Remove(operation); }
        }
    }

    private async Task Retire(uint operation)
    {
        while (true)
        {
            lock (_gate)
            {
                OpenTuiNative.CancelClipboardOperation(operation);
                _providerActive |= OpenTuiNative.DrainClipboardService(_service) == 1;
                if (OpenTuiNative.PollClipboardOperation(operation) != NativeClipboardOperationStatus.Pending &&
                    OpenTuiNative.DestroyClipboardOperation(operation) != NativeClipboardDestroyStatus.NotReady) return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(1), _clock, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private NativeClipboardResult ReadResult(uint operation, Kind kind, NativeClipboardOperationStatus status)
    {
        if (status == NativeClipboardOperationStatus.Read && kind == Kind.Read)
        {
            CheckCopy(OpenTuiNative.ClipboardMimeLength(operation, out var mimeLength));
            CheckCopy(OpenTuiNative.ClipboardDataLength(operation, out var dataLength));
            if (dataLength > Options.MaxReadBytes || dataLength > int.MaxValue) return new(NativeClipboardOperationStatus.LimitExceeded);
            if (mimeLength > 255) return Failed("Native clipboard returned an invalid MIME length.");
            var mime = new byte[checked((int)mimeLength)];
            var bytes = new byte[checked((int)dataLength)];
            CheckCopy(OpenTuiNative.CopyClipboardMime(operation, mime));
            CheckCopy(OpenTuiNative.CopyClipboardData(operation, bytes));
            return new(status, Utf8.GetString(mime), bytes);
        }
        if (status == NativeClipboardOperationStatus.Failed)
        {
            CheckCopy(OpenTuiNative.ClipboardErrorCode(operation, out var code));
            CheckCopy(OpenTuiNative.ClipboardDiagnosticLength(operation, out var length));
            var bytes = new byte[checked((int)length)];
            CheckCopy(OpenTuiNative.CopyClipboardDiagnostic(operation, bytes));
            return new(status, ErrorCode: code, Diagnostic: Utf8.GetString(bytes));
        }
        if (status is NativeClipboardOperationStatus.Unsupported or NativeClipboardOperationStatus.Cancelled or NativeClipboardOperationStatus.TimedOut ||
            kind == Kind.Read && status is NativeClipboardOperationStatus.Empty or NativeClipboardOperationStatus.LimitExceeded ||
            kind == Kind.Write && status == NativeClipboardOperationStatus.Written || kind == Kind.Clear && status == NativeClipboardOperationStatus.Cleared)
            return new(status);
        return Failed($"Native clipboard {kind} returned inapplicable status {status}.");
    }

    private void EnsureProviderPump()
    {
        if (_disposing || _providerRunning || !_providerActive) return;
        _providerRunning = true;
        _providerTask = PumpProvider();
    }
    private async Task PumpProvider()
    {
        var failed = false;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(8), _clock, CancellationToken.None).ConfigureAwait(true);
                lock (_gate)
                {
                    if (_disposing || !_providerActive) return;
                    _providerActive = OpenTuiNative.DrainClipboardService(_service) == 1;
                    if (!_providerActive) return;
                }
            }
        }
        catch { failed = true; throw; }
        finally
        {
            lock (_gate)
            {
                _providerRunning = false;
                if (!failed) EnsureProviderPump();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposing = true;
            foreach (var operation in _active.Keys) OpenTuiNative.CancelClipboardOperation(operation);
            return new(_disposeTask = Shutdown(_active.Values.ToArray(), _providerTask));
        }
    }
    private async Task Shutdown(Task<NativeClipboardResult>[] active, Task provider)
    {
        await Task.Yield();
        var failures = new List<Exception>();
        try { await Task.WhenAll(active.Cast<Task>().Append(provider)).ConfigureAwait(true); }
        catch (Exception exception) { failures.Add(exception); }
        try
        {
            if (_service != 0)
            {
                NativeClipboardShutdownStatus status;
                lock (_gate) status = OpenTuiNative.BeginClipboardShutdown(_service);
                while (status == NativeClipboardShutdownStatus.Pending)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1), _clock, CancellationToken.None).ConfigureAwait(true);
                    lock (_gate) status = OpenTuiNative.PollClipboardShutdown(_service);
                }
                if (status != NativeClipboardShutdownStatus.Ready) throw new InvalidOperationException("Native clipboard service became invalid during shutdown.");
                lock (_gate)
                {
                    if (OpenTuiNative.DestroyClipboardService(_service) != NativeClipboardDestroyStatus.Destroyed)
                        throw new InvalidOperationException("Native clipboard service could not be destroyed.");
                    _service = 0;
                }
            }
        }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count == 1) throw failures[0];
        if (failures.Count > 1) throw new AggregateException(failures);
    }
    private static void CheckCopy(NativeClipboardCopyStatus status)
    {
        if (status != NativeClipboardCopyStatus.Ok) throw new InvalidOperationException($"Could not copy native clipboard result ({status}).");
    }
    private static NativeClipboardResult Failed(string message) => new(NativeClipboardOperationStatus.Failed, Diagnostic: message);
}
