namespace OpenCode.Core.Tools;

using System.Text;

internal sealed partial class ShellSyntaxScanner
{
    private List<ScannedShellCommand> Backtick(int depth)
    {
        if (depth >= 32) throw Unsupported("command substitution nesting exceeds 32 levels");
        _index++;
        var body = new StringBuilder();
        while (_index < source.Length)
        {
            Checkpoint();
            var character = source[_index++];
            if (character == '`')
                // The portable source removes one escape layer before scanning.
                return Rescan(body.ToString()).List(depth + 1, requireStatement: true);
            if (character == '\\' && _index < source.Length && "$`\\\n".Contains(source[_index]))
            {
                character = source[_index++];
                if (character == '\n') continue;
            }
            body.Append(character);
        }
        throw Unsupported("unterminated backtick substitution");
    }

    private void ParameterExpansion(int depth, List<ScannedShellCommand> commands, bool doubleQuoted)
    {
        if (depth >= 32) throw Unsupported("parameter expansion nesting exceeds 32 levels");
        _index += 2;
        var prefix = _index < source.Length && source[_index] is '#' or '!';
        if (prefix) _index++;
        var start = _index;
        while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_')) _index++;
        if (_index == start && _index < source.Length && source[_index] is '@' or '*') _index++;
        if (_index == start) throw Unsupported("only named scalar/array parameter expansions are classified");
        if (_index < source.Length && source[_index] == '[')
        {
            _index++;
            ArrayKey(depth + 1, commands);
        }
        if (_index < source.Length && source[_index] == '}') { _index++; return; }
        if (prefix) throw Unsupported("length/key enumeration requires a complete named parameter");
        if (_index < source.Length && source[_index] == ':')
        {
            _index++;
            if (_index < source.Length && !"-+=?".Contains(source[_index]))
            {
                ArithmeticBody(depth + 1, "}", commands);
                return;
            }
        }
        if (_index >= source.Length || !"-+=?#%/".Contains(source[_index])) throw Unsupported("unclassified parameter expansion operator");
        var operation = source[_index++];
        if (operation is '#' or '%' or '/' && _index < source.Length && source[_index] == operation) _index++;
        while (_index < source.Length)
        {
            Checkpoint();
            if (source[_index] == '}') { _index++; return; }
            if (source[_index] is ' ' or '\t' or '\n') { _index++; continue; }
            if (doubleQuoted && source[_index] == '\'') throw Unsupported("single quotes in a double-quoted parameter fallback are not classified");
            _ = Word(depth + 1, commands, doubleQuoted);
        }
        throw Unsupported("unterminated parameter expansion");
    }
}
