namespace OpenCode.Core.Tools;

using System.Text;

internal sealed partial class ShellSyntaxScanner
{
    private sealed class HereDocument(string delimiter, bool quoted, bool tabs)
    {
        internal readonly string Delimiter = delimiter;
        internal readonly bool Quoted = quoted;
        internal readonly bool Tabs = tabs;
        internal int? Owner;
        internal int ResourceStart;
    }

    private HereDocument HereDocumentDelimiter(bool tabs)
    {
        var text = new StringBuilder();
        var quote = '\0';
        var quoted = false;
        var started = false;
        while (_index < source.Length)
        {
            Checkpoint();
            var character = source[_index];
            if (!started && character is ' ' or '\t') { _index++; continue; }
            if (!started && character == '#') throw Unsupported("missing here-document delimiter");
            if (quote == '\0' && " \t\n;&|()<>".Contains(character)) break;
            if (quote == '\0' && character == '$' && Peek(1) is '\'' or '"')
                throw Unsupported("ANSI/localized here-document delimiter quoting is not classified");
            if (character == quote) { quote = '\0'; _index++; continue; }
            if (character == '\\' && quote != '\'')
            {
                if (_index + 1 == source.Length) throw Unsupported("unterminated delimiter escape");
                if (Peek(1) == '\n') { _index += 2; continue; }
                if (quote == '\0' || "$`\"\\".Contains(Peek(1)))
                {
                    quoted = started = true;
                    text.Append(Peek(1));
                    _index += 2;
                    continue;
                }
            }
            if (quote == '\0' && character is '\'' or '"')
            {
                quote = character;
                quoted = started = true;
                _index++;
                continue;
            }
            text.Append(character);
            started = true;
            _index++;
        }
        if (!started || quote != '\0') throw Unsupported("missing or unterminated here-document delimiter");
        return new(text.ToString(), quoted, tabs);
    }

    private string HereDocumentBody(HereDocument document)
    {
        var body = new StringBuilder();
        var line = new StringBuilder();
        while (_index <= source.Length)
        {
            Checkpoint();
            var start = _index;
            while (_index < source.Length && source[_index] != '\n') _index++;
            var segment = source[start.._index];
            line.Append(document.Tabs ? segment.TrimStart('\t') : segment);
            var newline = _index < source.Length;
            if (newline) _index++;
            var slashes = 0;
            for (var offset = line.Length - 1; offset >= 0 && line[offset] == '\\'; offset--) slashes++;
            if (!document.Quoted && newline && slashes % 2 == 1)
            {
                line.Length--;
                continue;
            }
            if (line.ToString() == document.Delimiter) return body.ToString();
            body.Append(line);
            if (newline) body.Append('\n');
            line.Clear();
            if (!newline) break;
        }
        throw Unsupported("unterminated here-document body");
    }

    private ShellWord PowerShellHereString(int depth, List<ScannedShellCommand> commands)
    {
        var start = _index;
        var quote = Peek(1);
        _index += 2;
        while (_index < source.Length && source[_index] is ' ' or '\t') _index++;
        if (_index == source.Length || source[_index] is not ('\r' or '\n')) throw Unsupported("here-string header requires a newline");
        if (source[_index++] == '\r' && _index < source.Length && source[_index] == '\n') _index++;
        var body = _index;
        while (_index < source.Length)
        {
            Checkpoint();
            if ((_index == body || source[_index - 1] is '\r' or '\n') && source[_index] == quote && Peek(1) == '@')
            {
                var end = _index;
                if (end > body && source[end - 1] == '\n') end--;
                if (end > body && source[end - 1] == '\r') end--;
                var text = source[body..end];
                _index += 2;
                if (_index < source.Length && !" \t\r\n;|&<>)}".Contains(source[_index])) throw Unsupported("here-string token concatenation is not classified");
                if (quote == '"') commands.AddRange(Rescan(text).TextExpansions(depth + 1));
                // Expandable text is never used as a guessed executable/directory.
                return new(source[start.._index], text, quote == '\'');
            }
            _index++;
        }
        throw Unsupported("unterminated PowerShell here-string");
    }

    // Quotes in these bodies are data, not shell token delimiters. Only expansion
    // syntax enters the command scanner; literal lines never become commands.
    private List<ScannedShellCommand> TextExpansions(int depth)
    {
        if (depth > 32) throw Unsupported("here-body substitution nesting exceeds 32 levels");
        var commands = new List<ScannedShellCommand>();
        while (_index < source.Length)
        {
            Checkpoint();
            var character = source[_index];
            if (character == (powershell ? '`' : '\\'))
            {
                _index += _index + 1 < source.Length && (powershell || "$`\\\n".Contains(Peek(1))) ? 2 : 1;
                continue;
            }
            if (!powershell && character == '`') { commands.AddRange(Backtick(depth)); continue; }
            if (character == '$' && Peek(1) == '(')
            {
                if (!powershell && Peek(2) == '(') commands.AddRange(Arithmetic(depth));
                else
                {
                    _index += 2;
                    commands.AddRange(CommandSubstitution(depth + 1));
                }
                continue;
            }
            if (!powershell && character == '$' && Peek(1) == '{') { ParameterExpansion(depth, commands, false); continue; }
            if (!powershell && character == '$' && Peek(1) == '[') { commands.AddRange(Arithmetic(depth)); continue; }
            _index++;
        }
        return commands;
    }

    private bool PowerShellRedirectAhead()
    {
        var cursor = _index;
        if (cursor < source.Length && source[cursor] == '*') cursor++;
        else while (cursor < source.Length && char.IsAsciiDigit(source[cursor])) cursor++;
        return cursor < source.Length && source[cursor] == '>';
    }
}
