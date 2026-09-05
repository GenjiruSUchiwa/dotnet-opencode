namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    private bool SubscriptAssignmentAhead()
    {
        // Source recognizes a raw name[subscript]= or += word before scanning the
        // subscript. This lookahead does not resolve an array type or its contents.
        for (var cursor = _index + 1; cursor < source.Length; cursor++)
        {
            Checkpoint();
            if (source[cursor] != ']') continue;
            cursor++;
            if (cursor < source.Length && source[cursor] == '+') cursor++;
            if (cursor < source.Length && source[cursor] == '=') return true;
        }
        return false;
    }

    private void ArrayConstruction(int depth, List<ScannedShellCommand> commands)
    {
        if (depth >= 32) throw Unsupported("array construction nesting exceeds 32 levels");
        _index++;
        while (_index < source.Length)
        {
            Checkpoint();
            // Array bodies contain words, not a shell statement list. Comments at
            // element boundaries are also data-free, not nested executable source.
            if (source[_index] is ' ' or '\t' or '\n') { _index++; continue; }
            if (source[_index] == '#')
            {
                while (_index < source.Length && source[_index] != '\n') { Checkpoint(); _index++; }
                continue;
            }
            if (source[_index] == ')') { _index++; return; }
            if (source[_index] == '[')
            {
                _index++;
                ArrayKey(depth + 1, commands);
                if (_index < source.Length && source[_index] == '+') _index++;
                if (_index >= source.Length || source[_index++] != '=') throw Unsupported("array entry subscript requires '=' or '+='");
                if (_index >= source.Length || source[_index] is ' ' or '\t' or '\n' or ')') continue;
            }
            if (Word(depth + 1, commands).Assignment) throw Unsupported("nested array assignment words are not classified");
        }
        throw Unsupported("unterminated array construction");
    }

    private void ArrayKey(int depth, List<ScannedShellCommand> commands, char close = ']')
    {
        if (depth > 32) throw Unsupported("array key nesting exceeds 32 levels");
        // Indexed and associative arrays interpret quoted subscripts differently.
        // Do not guess an array's kind or reparse quoted data as shell commands.
        while (_index < source.Length)
        {
            Checkpoint();
            var character = source[_index];
            if (character == close) { _index++; return; }
            if (character == '\'')
            {
                var start = ++_index;
                while (_index < source.Length && source[_index] != '\'') { Checkpoint(); _index++; }
                if (_index == source.Length) throw Unsupported("unterminated quoted array key");
                if (source[start.._index].IndexOfAny(['$', '`']) >= 0)
                    throw Unsupported("quoted expansion-like array keys require array-kind knowledge");
                _index++;
                continue;
            }
            if (character == '"')
            {
                var start = ++_index;
                ArithmeticBody(depth + 1, "\"", commands);
                if (source[start.._index].Contains("\\$", StringComparison.Ordinal) || source[start.._index].Contains("\\`", StringComparison.Ordinal))
                    throw Unsupported("escaped expansion-like quoted keys require array-kind knowledge");
                continue;
            }
            if (character == '\\')
            {
                if (_index + 1 >= source.Length) throw Unsupported("unterminated array-key escape");
                if (Peek(1) is '$' or '`') throw Unsupported("escaped expansion-like array keys require array-kind knowledge");
                _index += 2;
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
            if (character == '$' && Peek(1) == '{') { ParameterExpansion(depth, commands, false); continue; }
            if (character is '(' or '[') { _index++; ArrayKey(depth + 1, commands, character == '(' ? ')' : ']'); continue; }
            if (character is ')' or ']' or ';') throw Unsupported("unclassified array subscript delimiter");
            _index++;
        }
        throw Unsupported("unterminated array key");
    }
}
