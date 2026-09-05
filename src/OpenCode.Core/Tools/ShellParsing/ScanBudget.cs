namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    private sealed class ScanBudget
    {
        internal int Remaining = 65536 * 32;
    }

    private ScanBudget _budget = new();

    private void Checkpoint(int cost = 1)
    {
        ct.ThrowIfCancellationRequested();
        _budget.Remaining -= cost;
        if (_budget.Remaining < 0) throw Unsupported("aggregate shell scan work exceeds the nested-expansion budget");
    }

    private ShellSyntaxScanner Rescan(string text)
    {
        Checkpoint(text.Length);
        return new ShellSyntaxScanner(text, powershell, ct) { _budget = _budget };
    }

    private List<ScannedShellCommand> CommandSubstitution(int depth)
    {
        // Source bashDelimited owns a separate here-document queue inside each
        // substitution. A newline here cannot consume an outer command's body.
        var pending = _pendingHereDocuments;
        _pendingHereDocuments = 0;
        try { return List(depth, ')', requireStatement: true); }
        finally { _pendingHereDocuments = pending; }
    }
}
