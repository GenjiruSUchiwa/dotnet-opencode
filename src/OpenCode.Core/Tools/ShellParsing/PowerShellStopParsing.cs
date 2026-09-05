namespace OpenCode.Core.Tools;

using System.Text;

internal sealed partial class ShellSyntaxScanner
{
    private ShellWord? PowerShellStopToken()
    {
        if (_index >= source.Length || source[_index] is '\'' or '"') return null;
        var start = _index;
        var cursor = _index;
        var value = new StringBuilder();
        var quote = '\0';
        while (cursor < source.Length)
        {
            Checkpoint();
            var character = source[cursor];
            if (quote == '\0' && " \t\r\n;|&(){}<>".Contains(character)) break;
            if (character == quote)
            {
                if (cursor + 1 < source.Length && source[cursor + 1] == quote) { value.Append(quote); cursor += 2; }
                else { quote = '\0'; cursor++; }
                continue;
            }
            if (quote == '\0' && character is '\'' or '"') { quote = character; cursor++; continue; }
            if (character == '`' && quote != '\'')
            {
                if (++cursor >= source.Length || source[cursor] is '\r' or '\n' or 'u') return null;
                character = source[cursor];
            }
            value.Append(character);
            cursor++;
            if (value.Length > 3 || !"--%".AsSpan().StartsWith(value.ToString(), StringComparison.Ordinal)) return null;
        }
        if (quote != '\0' || value.ToString() != "--%") return null;
        _index = cursor;
        return new(source[start.._index], "--%", true, true);
    }

    private ShellWord? PowerShellStopTail()
    {
        var start = _index;
        var quoted = false;
        while (_index < source.Length)
        {
            Checkpoint();
            var character = source[_index];
            if (character is '\r' or '\n' || (character == '|' && !quoted)) break;
            // Exactly source powerShellStopParsing: only double quotes toggle the
            // pipe boundary. Backticks, single quotes, $, redirects and braces are
            // literal tail text and never enter expression/command scanners.
            if (character == '"') quoted = !quoted;
            _index++;
        }
        var text = source[start.._index].Trim();
        return text.Length == 0 ? null : new(text, text, true, true);
    }
}
