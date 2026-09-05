namespace OpenTui.Blazor;

using System.Text.RegularExpressions;

internal static partial class TextareaPaste
{
    // The pinned OpenTUI portable strip-ansi/ansi-regex path (6.2.2), not a
    // parser or a rendering substitute. Regex replacement strips complete OSC/CSI.
    [GeneratedRegex(@"(?:\u001B\][\s\S]*?(?:\u0007|\u001B\u005C|\u009C))|[\u001B\u009B][\[\]()#;?]*(?:[0-9]{1,4}(?:[;:][0-9]{0,4})*)?[0-9A-PR-TZcf-nq-uy=><~]", RegexOptions.ECMAScript | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Ansi();
    internal static string Normalize(string text)
    {
        if (text.Length > TerminalTextEditing.MaximumPasteLength)
            throw new ArgumentException("Paste exceeds the configured UTF-16 length limit; nothing was inserted.", nameof(text));
        // TextDecoder in the source drops a leading UTF-8 BOM for each paste.
        if (text.StartsWith('\uFEFF')) text = text[1..];
        return TerminalTextEditing.NormalizePaste(Ansi().Replace(text, ""));
    }
}
