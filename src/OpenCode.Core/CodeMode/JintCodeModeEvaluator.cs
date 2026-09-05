namespace OpenCode.Core.CodeMode;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Acornima;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Interop;

/// <summary>
/// Pure-.NET, per-invocation JavaScript execution. A dedicated owner thread pumps
/// guest promises; asynchronous tools never enter the engine from completion threads.
/// This is capability confinement with cooperative limits, not OS isolation.
/// </summary>
public sealed class JintCodeModeEvaluator(TimeProvider? clock = null) : ICodeModeEvaluator
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public Task<CodeModeEvaluation> EvaluateAsync(string source, ICodeModeBindings bindings, CodeModeLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(limits);
        ct.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<CodeModeEvaluation>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Not a separate process and not a timeout wrapper around unrestricted eval:
        // the interpreter and every owner-thread pump share the same constraint.
        new Thread(() =>
        {
            try { completion.TrySetResult(Run(source, bindings, limits, ct)); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "opencode-codemode" }.Start();
        return completion.Task;
    }

    private CodeModeEvaluation Run(string source, ICodeModeBindings bindings, CodeModeLimits limits, CancellationToken ct)
    {
        using var deadline = _clock.CreateLinkedCancellationTokenSource(ct);
        deadline.CancelAfter(limits.TimeoutMilliseconds);
        var budget = new Budget(limits, deadline.Token, _clock);
        var queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var completed = "__oc_complete";
        var failed = "__oc_failed";
        CodeModeEvaluation? result = null;
        var observations = new CodeModePromiseObservations();
        Action? closeGenerators = null;
        Func<JsValue, CodeModeDiagnostic>? normalize = null;
        try
        {
            if (string.IsNullOrWhiteSpace(source)) return new(default, new("ParseError", "Code cannot be empty."));
            if (Encoding.UTF8.GetByteCount(source) > limits.MaxSourceBytes) return new(default, new("InvalidDataValue", "Source exceeds the host byte limit."));
            var prepared = JintCodeModeSyntax.Prepare(source, completed, failed, limits, budget.Checkpoint);
            var resolver = new JintCodeModeRealm.Resolver();
            using var engine = new Engine(options =>
            {
                options.Strict();
                options.Host.StringCompilationAllowed = false;
                options.Interop.Enabled = false;
                options.Interop.AllowGetType = false;
                options.Interop.AllowSystemReflection = false;
                options.Interop.AllowWrite = false;
                options.Interop.WrapObjectHandler = (_, _, _) => throw new InvalidOperationException("CLR object wrapping is forbidden in program execution.");
                options.AgentCanSuspend = false;
                options.Constraints.StackOverflowGuard = true;
                options.LimitRecursion(limits.MaxRecursionDepth);
                options.MaxArraySize((uint)Math.Min(limits.MaxBoundaryBytes, 100000));
                options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(Math.Min(limits.TimeoutMilliseconds, 250));
                options.Constraint(budget);
                options.ReferenceResolver = resolver;
            });
            var errorValues = new CodeModeErrorValues(engine);
            var realm = new JintCodeModeRealm(engine, bindings, limits, budget.Checkpoint, errorValues, observations, (path, args) =>
            {
                budget.Checkpoint();
                // Explicit promises, never automatic TaskInterop/FromObject. Only JSON
                // data is queued across threads. The capture stays alive until outer
                // CodeModeTool closes admission and joins the owned leaf calls.
                var ordinal = observations.Reserve();
                var (promise, resolve, reject) = engine.Advanced.RegisterPromise();
                observations.Track(promise, ordinal);
                Task<JsonElement> task;
                try { task = bindings.CallAsync(path, args); }
                catch (CodeModeDiagnosticException error)
                {
                    reject(errorValues.Runtime(error.Diagnostic));
                    return promise;
                }
                _ = task.ContinueWith(settled =>
                {
                    queue.Writer.TryWrite(() =>
                    {
                        budget.Checkpoint();
                        if (settled.IsCompletedSuccessfully)
                        {
                            JsValue value;
                            try
                            {
                                value = JintCodeModeRealm.FromJson(engine, CodeModeData.Copy(settled.Result, limits.MaxBoundaryBytes, "Tool result"));
                            }
                            catch (CodeModeDiagnosticException invalidOutput)
                            {
                                reject(errorValues.Runtime(new("InvalidToolOutput", invalidOutput.Message)));
                                return;
                            }
                            resolve(value);
                            return;
                        }
                        if (settled.IsCanceled) throw new OperationCanceledException(deadline.Token);
                        var error = settled.Exception!.InnerExceptions.Count == 1 ? settled.Exception.InnerException! : settled.Exception;
                        if (error is CodeModeDiagnosticException diagnostic)
                        {
                            reject(errorValues.Runtime(diagnostic.Diagnostic));
                            return;
                        }
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    });
                    // Observe even when the program has already closed its queue.
                    _ = settled.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return promise;
            });
            realm.Install();
            closeGenerators = realm.CloseGenerators;
            normalize = realm.NormalizeFailure;
            resolver.Realm = realm;
            engine.Advanced.PromiseRejectionTracker += (_, item) =>
            {
                if (observations.Frozen) return;
                if (item.Operation.ToString() == "Handle") { observations.Observe(item.Promise); return; }
                observations.Rejected(item.Promise, realm.NormalizeFailure(item.Value ?? JsValue.Undefined));
            };
            engine.SetValue(completed, new ClrFunction(engine, completed, (_, args) =>
            {
                result = new(realm.ToJson(args.Length == 0 ? JsValue.Undefined : args[0], nullify: true));
                budget.Completed = true;
                return JsValue.Undefined;
            }));
            engine.SetValue(failed, new ClrFunction(engine, failed, (_, args) =>
            {
                result = new(default, realm.NormalizeFailure(args.Length == 0 ? JsValue.Undefined : args[0]));
                budget.Completed = true;
                return JsValue.Undefined;
            }));
            // The final callbacks capture the result before an internal control
            // exception stops any remaining microtasks. A completed main program
            // must not wait for every un-awaited manual promise to settle.
            try
            {
                engine.Execute(prepared);
                while (result is null)
                {
                    budget.Checkpoint();
                    queue.Reader.ReadAsync(deadline.Token).AsTask().GetAwaiter().GetResult()();
                    engine.Advanced.ProcessTasks();
                }
            }
            catch (ProgramCompletedException) when (result is not null) { }
            return result! with { Warnings = observations.Snapshot() };
        }
        catch (ParseErrorException error) { return new(default, new("ParseError", error.Message)); }
        catch (CodeModeDiagnosticException error) { return new(default, error.Diagnostic); }
        catch (CodeModeLimitException error) { return new(default, error.Diagnostic); }
        catch (JavaScriptException error) { return new(default, normalize?.Invoke(error.Error) ?? new("ExecutionFailure", error.Message)); }
        catch (RecursionDepthOverflowException error) { return new(default, new("ExecutionFailure", error.Message)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        { return result ?? new(default, new("TimeoutExceeded", $"Execution timed out after {limits.TimeoutMilliseconds}ms.")); }
        finally { closeGenerators?.Invoke(); queue.Writer.TryComplete(); }
    }

    private sealed class ProgramCompletedException : Exception;

    private sealed class Budget(CodeModeLimits limits, CancellationToken ct, TimeProvider clock) : Constraint
    {
        private readonly long _started = clock.GetTimestamp();
        private readonly long _allocated = GC.GetAllocatedBytesForCurrentThread();
        private long _checks;
        internal bool Completed;
        public override bool IsAmortizable => false;
        // Jint resets per engine entry. The program's budget deliberately does not.
        public override void Reset() { }
        public override void Check()
        {
            Checkpoint();
            if (++_checks > limits.MaxExecutionChecks)
                throw new CodeModeLimitException(new("ExecutionFailure", "Execution checkpoint budget exceeded."));
        }
        internal void Checkpoint()
        {
            if (Completed) throw new ProgramCompletedException();
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(_started).TotalMilliseconds >= limits.TimeoutMilliseconds)
                throw new CodeModeLimitException(new("TimeoutExceeded", $"Execution timed out after {limits.TimeoutMilliseconds}ms."));
            if (GC.GetAllocatedBytesForCurrentThread() - _allocated > limits.MaxAllocatedBytes)
                throw new CodeModeLimitException(new("ExecutionFailure", "Owner-thread allocation budget exceeded (not a hard heap quota)."));
        }
    }
}
