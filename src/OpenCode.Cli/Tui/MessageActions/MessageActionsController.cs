namespace OpenCode.Cli.Tui.MessageActions;

using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Schema;
using OpenTui.Blazor;

public sealed class MessageActionsController(ITextClipboard clipboard, Func<MessageTarget, SessionMessage?> readMessage, MessageActionServices services)
{
    public IReadOnlyList<DialogSelectOption<string>> Options(MessageTarget target)
    {
        var message = readMessage(target);
        if (message is null) return [];
        var options = new List<DialogSelectOption<string>>();
        if (services.Jump is not null) options.Add(new("message.jump", "Jump to", "view message in session"));
        if (services.StageRevert is not null && services.Reconcile is not null && (services.CanMutate?.Invoke(target) ?? true))
            options.Add(new("session.revert", "Revert", "undo messages and file changes"));
        options.Add(new("message.copy", "Copy", "message text to clipboard"));
        if (message is UserMessage && services.ForkBefore is not null && services.OpenFork is not null && (services.CanMutate?.Invoke(target) ?? true))
            options.Add(new("session.fork", "Fork", "create a new session"));
        return options;
    }

    public Task<bool> CopyCodeAsync(string code, CancellationToken cancellationToken = default) => CopyText(code, cancellationToken);
    public Task<bool> CopyMessageAsync(MessageTarget target, CancellationToken cancellationToken = default) =>
        readMessage(target) is { } message ? CopyText(Text(message), cancellationToken) : Task.FromResult(false);

    public async Task<bool> ExecuteAsync(string action, MessageTarget target, CancellationToken cancellationToken = default)
    {
        if (!Options(target).Any(option => option.Value == action)) return false;
        try
        {
            switch (action)
            {
                case "message.copy": return await CopyMessageAsync(target, cancellationToken);
                case "message.jump": await services.Jump!(target); return true;
                case "session.revert":
                    if (readMessage(target) is UserMessage user) services.RestorePrompt?.Invoke(user);
                    await services.StageRevert!(target, cancellationToken);
                    await services.Reconcile!(target, cancellationToken);
                    return true;
                case "session.fork":
                    if (readMessage(target) is not UserMessage original) return false;
                    var session = await services.ForkBefore!(target, cancellationToken);
                    await services.OpenFork!(session, original);
                    return true;
                default: return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { services.ReportError(exception); return false; }
    }

    private async Task<bool> CopyText(string text, CancellationToken cancellationToken)
    {
        try { await clipboard.WriteTextAsync(text, cancellationToken); return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { services.ReportError(exception); return false; }
    }

    // routes/session/dialog-message.tsx: copy canonical text, not rendered labels,
    // tool output, reasoning, timestamps, or a formatted transcript approximation.
    public static string Text(SessionMessage message) => message switch
    {
        UserMessage user => user.Text,
        AssistantMessage assistant => string.Join('\n', assistant.Content.OfType<AssistantTextContent>().Select(part => part.Text)),
        SyntheticMessage synthetic => synthetic.Text,
        SystemMessage system => system.Text,
        _ => ""
    };
}
