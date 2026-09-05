namespace OpenCode.Core.Tools.Builtins;

using System.ComponentModel;
using System.Text.Json;
using OpenCode.Core.Permissions;
using OpenCode.Core.Shell;
using OpenCode.Schema;

/// <summary>Permission-owning tool leaf over the same Location shell lifecycle used by Server.</summary>
public sealed class ShellTool
{
    private readonly IToolShellPolicy? _policy;
    private readonly Func<ShellRuntime>? _runtime;
    private readonly IShellToolJobs? _jobs;

    // Preserve current factory compilation until its owner supplies runtime. The old process and
    // environment arguments are never executed: no separate-process or instructionless fallback.
    public ShellTool(IToolShellPolicy? policy = null, ShellProcessSource? process = null,
        Func<SessionId, IReadOnlyDictionary<string, string>?>? environment = null,
        Func<ShellRuntime>? runtime = null, IShellToolJobs? jobs = null)
    { _policy = policy; _runtime = runtime; _jobs = jobs; }

    public ToolInfo Create() => ToolInfo.FromJson(Name, Description, InputSchema, ExecuteAsync, ShellToolOutput.Schema, new ToolOptions(CodeMode: false));
    public string Name => "shell";
    public string Description => "Execute a shell command and return its output. Quote file paths containing spaces or special characters. " +
        "Prefer dedicated tools over shell commands when possible. When output is large, the full result is saved to a file and a truncated preview is returned. " +
        "Rely on automatic truncation unless filtering the output is more useful. Commands accept an optional timeout; background commands have no timeout by default. " +
        (_jobs?.SupportsBackground == true
            ? "Background commands return immediately, and you will be notified when they complete. DO NOT poll its progress. "
            : "Background commands and foreground promotion are unavailable until the host supplies durable Job/completion integration. ") +
        (_policy is LocalLiteralShellPolicy ? "This adapter accepts one literal command only; no pipelines, redirection or substitutions."
            : LocalShellPolicy.Grammar.Replace("background jobs", "shell background operators", StringComparison.Ordinal));
    public JsonElement InputSchema => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "Shell command string to execute" },
            workdir = new { type = "string", description = "Working directory to execute the command in. Defaults to the current working directory. When possible, avoid changing directories in the command and set the working directory here instead." },
            timeout = new { type = "integer", minimum = 0, description = "Timeout in milliseconds. Set to 0 to disable the timeout. Defaults to 120000 for foreground commands. Background commands have no timeout by default." },
            background = new { type = "boolean", description = "Run the command in the background and return immediately. You will be notified when it completes. DO NOT poll its progress." }
        },
        required = new[] { "command" }
    });

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var args = new ToolInput(input);
        var command = args.String("command");
        var background = args.Boolean("background");
        var timeout = args.Integer("timeout", background ? 0 : 120_000, 0);
        var workdir = args.OptionalString("workdir");
        if (_policy is null || _runtime is null) throw new NotSupportedException("shell requires the shared Location ShellRuntime and scanned permission policy; no independent process fallback is available.");
        try
        {
            return await ShellToolExecution.ExecuteAsync(_runtime(), _policy, _jobs, command, workdir, timeout, background, context, ct);
        }
        catch (PermissionBlockedException denial) { throw new ToolExecutionException($"Unable to execute command: {denial.Detail}", denial); }
        catch (PermissionCorrectedException correction) { throw new ToolExecutionException(correction.Feedback, correction); }
        catch (ShellNotFoundException error) { throw new ToolExecutionException(error.Message, error); }
        catch (NotSupportedException error) { throw new ToolExecutionException(error.Message, error); }
        catch (IOException error) { throw new ToolExecutionException($"Unable to execute command: {error.Message}", error); }
        catch (UnauthorizedAccessException error) { throw new ToolExecutionException($"Unable to execute command: {error.Message}", error); }
        catch (Win32Exception error) { throw new ToolExecutionException($"Unable to execute command: {error.Message}", error); }
    }
}
