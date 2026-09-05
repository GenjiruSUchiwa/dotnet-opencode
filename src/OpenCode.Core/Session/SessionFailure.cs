namespace OpenCode.Core.Session;

using OpenCode.Core.Llm;
using OpenCode.Core.Permissions;
using OpenCode.Core.Tools;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Schema;

internal sealed class SessionStepFailedException(SessionStructuredError error) : Exception(error.Message)
{
    internal SessionStructuredError Error { get; } = error;
}

/// <summary>Selected to-session-error.ts mappings; never persists HTTP headers, URL or response body.</summary>
internal static class SessionFailure
{
    internal static SessionStructuredError From(Exception error)
    {
        if (error is SessionStepFailedException step) return step.Error;
        if (error is LlmException provider)
        {
            int? status = provider.Reason.Http is { } http && (int)http.Status is >= 100 and <= 599 ? (int)http.Status : null;
            return new SessionStructuredError(provider.Reason switch
            {
                LlmFailure.RateLimit => "provider.rate-limit",
                LlmFailure.Authentication => "provider.auth",
                LlmFailure.QuotaExceeded => "provider.quota",
                LlmFailure.ContentPolicy => "provider.content-filter",
                LlmFailure.Transport => "provider.transport",
                LlmFailure.ProviderInternal => "provider.internal",
                LlmFailure.InvalidProviderOutput => "provider.invalid-output",
                LlmFailure.InvalidRequest or LlmFailure.Unsupported => "provider.invalid-request",
                _ => "provider.unknown"
            }, provider.Reason.Message, status);
        }
        if (error is PermissionBlockedException) return new("permission.rejected", error.Message);
        if (error is ToolExecutionException tool)
        {
            if (tool.InnerException is not null)
            {
                var unwrapped = From(tool.InnerException);
                return unwrapped.Message.Length > 0 ? unwrapped : unwrapped with { Type = "tool.execution", Message = tool.Message };
            }
            return new("tool.execution", tool.Message);
        }
        if (error is QuestionCancelledException) return new("aborted", error.Message);
        if (error is OperationCanceledException) return new("aborted", "Step interrupted");
        return new("unknown", error.Message);
    }
}
