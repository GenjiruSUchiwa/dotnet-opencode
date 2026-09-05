namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    // Like source bashExpansion: balance syntax and collect embedded commands;
    // never calculate numbers, resolve variable contents or select loop branches.
    private List<ScannedShellCommand> Arithmetic(int depth, bool header = false)
    {
        var commands = new List<ScannedShellCommand>();
        var bracket = source.AsSpan(_index).StartsWith("$[", StringComparison.Ordinal);
        _index += source[_index] == '$' ? bracket ? 2 : 3 : 2;
        ArithmeticBody(depth + 1, bracket ? "]" : "))", commands, header);
        return commands;
    }

    private void ArithmeticBody(int depth, string close, List<ScannedShellCommand> commands, bool header = false)
    {
        if (depth > 32) throw Unsupported("arithmetic expansion nesting exceeds 32 levels");
        var clauses = 0;
        while (_index < source.Length)
        {
            Checkpoint();
            if (source.AsSpan(_index).StartsWith(close, StringComparison.Ordinal))
            {
                if (header && clauses != 2) throw Unsupported("arithmetic for requires three clauses");
                _index += close.Length;
                return;
            }
            var character = source[_index];
            if (character == '\\')
            {
                if (_index + 1 >= source.Length) throw Unsupported("unterminated arithmetic escape");
                // Double-quoted expansions only remove escapes for special characters.
                _index += close != "\"" || "$`\\\"\n".Contains(Peek(1)) ? 2 : 1;
                continue;
            }
            if (character == '`') { commands.AddRange(Backtick(depth)); continue; }
            if (character == '$' && Peek(1) == '(')
            {
                if (Peek(2) == '(') commands.AddRange(Arithmetic(depth));
                else { _index += 2; commands.AddRange(CommandSubstitution(depth + 1)); }
                continue;
            }
            if (character == '$' && Peek(1) == '[') { commands.AddRange(Arithmetic(depth)); continue; }
            if (character == '$' && Peek(1) == '{') { ParameterExpansion(depth, commands, close == "\""); continue; }
            if (character == '"') { _index++; ArithmeticBody(depth + 1, "\"", commands); continue; }
            if (close != "\"")
            {
                if (character is '(' or '[')
                {
                    _index++;
                    ArithmeticBody(depth + 1, character == '(' ? ")" : "]", commands);
                    continue;
                }
                if (character is ')' or ']') throw Unsupported("mismatched arithmetic delimiter");
                if (character == ';')
                {
                    if (!header || ++clauses > 2) throw Unsupported("unexpected arithmetic clause separator");
                }
                // Single quotes do not suppress expansion discovery in source
                // arithmetic modes. They are not scanned as shell quoted words.
            }
            _index++;
        }
        throw Unsupported("unterminated arithmetic expansion");
    }
}
