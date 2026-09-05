namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    private bool PowerShellAtom(int depth, List<ScannedShellCommand> commands)
    {
        if (depth > 32) throw Unsupported("PowerShell expression nesting exceeds 32 levels");
        Checkpoint();
        if (_index >= source.Length) throw Unsupported("missing PowerShell expression operand");
        var complex = false;
        if (source[_index] == '[')
        {
            PowerShellType(depth, commands);
            complex = true;
            Trivia(false);
            // A type literal or static receiver ends here. Otherwise it is a cast.
            if (_index < source.Length && source[_index] is '+' or '-' &&
                (char.IsAsciiDigit(Peek(1)) || Peek(1) is '$' or '(' or '['))
            {
                _index++;
                Trivia(false);
                if (!PowerShellOperandStart()) throw Unsupported("cast requires an operand");
            }
            if (PowerShellOperandStart()) complex |= PowerShellAtom(depth + 1, commands);
        }
        else if (source[_index] == '$' && Peek(1) == '(')
        {
            _index++;
            commands.AddRange(Group(depth, '(', true));
        }
        else if (source[_index] == '$') PowerShellVariable();
        else if (source[_index] == '(') commands.AddRange(Group(depth, '(', true));
        else if (source[_index] == '{')
        {
            commands.AddRange(Group(depth, '{'));
            complex = true;
        }
        else if (source[_index] == '@' && Peek(1) == '(')
        {
            _index++;
            commands.AddRange(Group(depth, '('));
            complex = true;
        }
        else if (source[_index] == '@' && Peek(1) == '{')
        {
            PowerShellHash(depth, commands);
            complex = true;
        }
        else if (source[_index] is '\'' or '"' || (source[_index] == '@' && Peek(1) is '\'' or '"'))
            _ = Word(depth, commands, expressionString: true);
        else if (char.IsAsciiDigit(source[_index]))
        {
            while (_index < source.Length && char.IsAsciiDigit(source[_index])) _index++;
            if (_index < source.Length && source[_index] == '.' && char.IsAsciiDigit(Peek(1)))
            {
                _index++;
                while (_index < source.Length && char.IsAsciiDigit(source[_index])) _index++;
            }
        }
        else throw Unsupported("unclassified PowerShell expression operand");
        return PowerShellSuffix(depth, commands) || complex;
    }

    private bool PowerShellSuffix(int depth, List<ScannedShellCommand> commands)
    {
        var found = false;
        while (_index < source.Length)
        {
            Checkpoint();
            if (source[_index] == '[')
            {
                found = true;
                _index++;
                PowerShellExpressionItems(depth, commands, ']', false);
                continue;
            }
            var member = source[_index] == '.' && Peek(1) != '.';
            var statik = source[_index] == ':' && Peek(1) == ':';
            if (!member && !statik) break;
            found = true;
            _index += statik ? 2 : 1;
            var start = _index;
            while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_')) _index++;
            if (!Identifier(source[start.._index])) throw Unsupported("member names must be literal identifiers");
            if (_index < source.Length && source[_index] == '(')
            {
                _index++;
                PowerShellExpressionItems(depth, commands, ')', true);
            }
        }
        return found;
    }

    private void PowerShellExpressionItems(int depth, List<ScannedShellCommand> commands, char close, bool empty, bool named = false)
    {
        Trivia();
        if (_index < source.Length && source[_index] == close && empty) { _index++; return; }
        while (true)
        {
            if (depth >= 32) throw Unsupported("PowerShell expression nesting exceeds 32 levels");
            if (named)
            {
                var saved = _index;
                while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_')) _index++;
                var name = source[saved.._index];
                if (Identifier(name))
                {
                    Trivia(false);
                    if (_index < source.Length && source[_index] == '=') { _index++; Trivia(); }
                    else _index = saved;
                }
                else _index = saved;
            }
            var value = PowerShellScalar(depth + 1, commaStops: true);
            if (value is null) throw Unsupported("method/index/attribute values require expressions; parenthesize command pipelines");
            commands.AddRange(value);
            Trivia();
            if (_index >= source.Length) throw Unsupported("unterminated expression list");
            if (source[_index] == close) { _index++; return; }
            if (source[_index++] != ',') throw Unsupported("expected a comma or closing expression delimiter");
            Trivia();
        }
    }

    private void PowerShellType(int depth, List<ScannedShellCommand> commands, bool attributes = false)
    {
        if (depth > 32) throw Unsupported("PowerShell type nesting exceeds 32 levels");
        Checkpoint();
        if (_index >= source.Length || source[_index++] != '[') throw Unsupported("expected a type constraint");
        TypeName(depth);
        if (attributes && _index < source.Length && source[_index] == '(')
        {
            _index++;
            PowerShellExpressionItems(depth, commands, ')', true, named: true);
        }
        Trivia(false);
        if (_index >= source.Length || source[_index++] != ']') throw Unsupported("unclassified type or attribute syntax");

        void TypeName(int level)
        {
            if (level > 32) throw Unsupported("PowerShell type nesting exceeds 32 levels");
            Checkpoint();
            Trivia(false);
            var start = _index;
            while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] is '_' or '.' or '+')) _index++;
            if (start == _index || !source[start.._index].Split('.', '+').All(Identifier)) throw Unsupported("type names must be literal qualified identifiers");
            Trivia(false);
            while (_index < source.Length && source[_index] == '[')
            {
                _index++;
                Trivia(false);
                if (_index < source.Length && source[_index] == ']') { _index++; continue; }
                TypeName(level + 1);
                Trivia(false);
                while (_index < source.Length && source[_index] == ',') { _index++; TypeName(level + 1); Trivia(false); }
                if (_index >= source.Length || source[_index++] != ']') throw Unsupported("unterminated generic type arguments");
            }
        }
    }

    private void PowerShellHash(int depth, List<ScannedShellCommand> commands)
    {
        _index += 2;
        while (true)
        {
            Trivia();
            if (_index >= source.Length) throw Unsupported("unterminated hashtable");
            if (source[_index] == '}') { _index++; return; }
            if (source[_index] is '\'' or '"')
            {
                var key = Word(depth, commands, expressionString: true);
                if (!key.Literal) throw Unsupported("computed hashtable keys are not classified");
            }
            else
            {
                var start = _index;
                while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] == '_')) _index++;
                if (!Identifier(source[start.._index])) throw Unsupported("hashtable keys must be literal identifiers or strings");
            }
            Trivia(false);
            if (_index >= source.Length || source[_index++] != '=') throw Unsupported("hashtable entry requires '='");
            commands.AddRange(List(depth + 1, stops: [";", "\n", "\r", "}"], requireStatement: true));
            if (_index >= source.Length) throw Unsupported("unterminated hashtable entry");
            if (source[_index] != '}') _index++;
        }
    }

    private bool PowerShellOperandStart() => _index < source.Length &&
        (source[_index] is '$' or '\'' or '"' or '(' or '[' or '{' ||
         (source[_index] == '@' && Peek(1) is '(' or '{' or '\'' or '"') || char.IsAsciiDigit(source[_index]));
}
