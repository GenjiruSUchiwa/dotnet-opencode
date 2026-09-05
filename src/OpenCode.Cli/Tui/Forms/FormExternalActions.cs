namespace OpenCode.Cli.Tui.Forms;

using System.Diagnostics;

public static class FormExternalActions
{
    /// <summary>Explicit user action only. Launch failures propagate to the form instead of acknowledging an unopened link.</summary>
    public static Task OpenAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The external form URL must use HTTP or HTTPS.", nameof(url));
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
