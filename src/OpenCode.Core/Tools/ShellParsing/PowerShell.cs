namespace OpenCode.Core.Tools;

internal sealed partial class ShellSyntaxScanner
{
    private List<ScannedShellCommand>? PowerShellStatement(int depth)
    {
        var keyword = Keyword();
        if (keyword == "param")
        {
            var start = _index;
            _index += 5;
            var commands = PowerShellParameters(depth);
            commands.Add(new(source[start.._index], [], false));
            return commands;
        }
        if (source[_index] == '[')
        {
            var start = _index;
            var attributes = new List<ScannedShellCommand>();
            while (_index < source.Length && source[_index] == '[')
            {
                PowerShellType(depth, attributes, attributes: true);
                Trivia();
            }
            if (Keyword() == "param")
            {
                _index += 5;
                attributes.AddRange(PowerShellParameters(depth));
                attributes.Add(new(source[start.._index].Trim(), [], false));
                return attributes;
            }
            _index = start; // Ordinary type/cast expression, not an attribute preamble.
        }
        if (keyword == "switch") return PowerShellSwitch(depth);
        if (keyword == "if")
        {
            _index += 2;
            var commands = Group(depth, '(', true);
            commands.AddRange(Group(depth, '{'));
            while (Continuation("elseif"))
            {
                _index += 6;
                commands.AddRange(Group(depth, '(', true));
                commands.AddRange(Group(depth, '{'));
            }
            if (Continuation("else")) { _index += 4; commands.AddRange(Group(depth, '{')); }
            return commands;
        }
        if (keyword is "while" or "for" or "foreach")
        {
            // foreach is also a command alias when it has no parenthesized header.
            var saved = _index;
            _index += keyword.Length;
            Trivia();
            if (keyword == "foreach" && (_index >= source.Length || source[_index] != '(')) { _index = saved; return null; }
            if (keyword != "foreach")
            {
                var commands = keyword == "for" ? PowerShellForHeader(depth) : Group(depth, '(', true);
                commands.AddRange(Group(depth, '{'));
                return commands;
            }
            _index++;
            Trivia();
            PowerShellVariable();
            Expect("in");
            var items = List(depth + 1, ')', requireStatement: true);
            items.AddRange(Group(depth, '{'));
            return items;
        }
        if (keyword == "do")
        {
            _index += 2;
            var commands = Group(depth, '{');
            Trivia();
            keyword = Keyword();
            if (keyword is not ("while" or "until")) throw Unsupported("do requires while or until");
            _index += keyword.Length;
            commands.AddRange(Group(depth, '(', true));
            return commands;
        }
        if (keyword is "function" or "filter")
        {
            var declaration = _index;
            _index += keyword.Length;
            Trivia();
            var start = _index;
            while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] is '_' or '-')) _index++;
            if (_index == start || !char.IsAsciiLetter(source[start])) throw Unsupported("function/filter requires a simple literal name");
            Trivia();
            var commands = new List<ScannedShellCommand>();
            var parameters = _index < source.Length && source[_index] == '(';
            if (parameters)
            {
                commands.AddRange(PowerShellParameters(depth));
            }
            commands.AddRange(Group(depth, '{'));
            if (parameters) commands.Add(new(source[declaration.._index], [], false));
            return commands;
        }
        if (keyword == "try")
        {
            _index += 3;
            var commands = Group(depth, '{');
            var handler = false;
            while (Continuation("catch"))
            {
                handler = true;
                var start = _index;
                _index += 5;
                Trivia();
                if (_index < source.Length && source[_index] == '[')
                {
                    PowerShellType(depth, commands);
                    Trivia();
                    while (_index < source.Length && source[_index] == ',') { _index++; Trivia(); PowerShellType(depth, commands); Trivia(); }
                    commands.Add(new(source[start.._index].Trim(), [], false));
                }
                commands.AddRange(Group(depth, '{'));
            }
            if (Continuation("finally")) { handler = true; _index += 7; commands.AddRange(Group(depth, '{')); }
            if (!handler) throw Unsupported("try requires catch or finally");
            return commands;
        }
        if (keyword is "return" or "throw" or "exit")
        {
            _index += keyword.Length;
            Trivia(false);
            if (AtExpressionEnd()) return [];
            return PowerShellScalar(depth); // A command-valued pipeline is scanned by the caller when this returns null.
        }
        if (keyword is "break" or "continue")
        {
            _index += keyword.Length;
            Trivia(false);
            if (!AtExpressionEnd()) throw Unsupported("labelled break/continue is not classified");
            return [];
        }
        if (source[_index] == '$' && Peek(1) != '(')
        {
            var saved = _index;
            PowerShellVariable();
            Trivia(false);
            if (_index < source.Length && (source[_index] == '=' || ("+-*/%".Contains(source[_index]) && Peek(1) == '=')))
            {
                _index += source[_index] == '=' ? 1 : 2;
                Trivia();
                if (AtExpressionEnd()) throw Unsupported("missing assignment value");
                var value = PowerShellScalar(depth);
                if (value is not null && value.Any(item => item.Words.Count == 0)) value.Add(new(source[saved.._index].Trim(), [], false));
                return value;
            }
            _index = saved;
        }
        return PowerShellScalar(depth);
    }

    // This is a classifier, not a PowerShell evaluator. Only scalar operands and
    // known operators are accepted. Every command-bearing parenthesis is scanned.
    private List<ScannedShellCommand>? PowerShellScalar(int depth, bool commaStops = false)
    {
        if (_index >= source.Length) return null;
        if (depth > 32) throw Unsupported("PowerShell expression nesting exceeds 32 levels");
        var start = _index;
        var first = source[_index];
        if (!(PowerShellOperandStart() || first == ',' ||
            (first is '+' or '-' && (char.IsAsciiDigit(Peek(1)) || Peek(1) == '$')) ||
            StartsOperator("-not") || StartsOperator("-bnot"))) return null;
        var commands = new List<ScannedShellCommand>();
        var operand = true;
        var complex = false;
        var singleVariable = first == '$' && Peek(1) != '(';
        while (!AtExpressionEnd())
        {
            Checkpoint();
            Trivia(false);
            if (AtExpressionEnd()) break;
            if (!operand && commaStops && source[_index] == ',') break;
            if (!operand)
            {
                if (PowerShellRedirectAhead()) break;
                if (source.AsSpan(_index).StartsWith("++", StringComparison.Ordinal) || source.AsSpan(_index).StartsWith("--", StringComparison.Ordinal))
                {
                    if (!singleVariable) throw Unsupported("increment/decrement requires one scalar variable");
                    _index += 2;
                    Trivia(false);
                    if (!AtExpressionEnd()) throw Unsupported("unexpected token after increment/decrement");
                    break;
                }
                var operation = ScalarOperators.FirstOrDefault(StartsOperator);
                if (operation is null) throw Unsupported("unclassified PowerShell scalar operator or member access");
                _index += operation.Length;
                if (operation is "," or ".." or "=" or "+=" or "-=" or "*=" or "/=" or "%=") complex = true;
                singleVariable = false;
                operand = true;
                continue;
            }
            if (StartsOperator("-not") || StartsOperator("-bnot"))
            {
                singleVariable = false;
                _index += StartsOperator("-not") ? 4 : 5;
                continue;
            }
            if (source[_index] is '+' or '-') { singleVariable = false; _index++; continue; }
            if (source[_index] == ',') { complex = true; singleVariable = false; _index++; continue; }
            var atom = PowerShellAtom(depth, commands);
            complex |= atom;
            singleVariable &= !atom;
            operand = false;
            Trivia(false);
        }
        if (operand) throw Unsupported("missing PowerShell scalar operand");
        if (complex || commands.Any(item => item.Words.Count == 0)) commands.Add(new(source[start.._index].Trim(), [], false));
        return commands;
    }

    private static readonly string[] ScalarOperators = ["-notcontains", "-notin", "-contains", "-and", "-xor", "-or", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-in", "-is", "-as", "+=", "-=", "*=", "/=", "%=", "..", "=", ",", "+", "-", "*", "/", "%"];

    private List<ScannedShellCommand> PowerShellForHeader(int depth)
    {
        if (_index >= source.Length || source[_index++] != '(') throw Unsupported("for requires a parenthesized header");
        var commands = new List<ScannedShellCommand>();
        for (var clause = 0; clause < 3; clause++)
        {
            commands.AddRange(List(depth + 1, stops: [";", ")"]));
            if (_index >= source.Length || source[_index++] != (clause == 2 ? ')' : ';'))
                throw Unsupported("for requires exactly three header clauses");
        }
        return commands;
    }

    private bool Continuation(string keyword)
    {
        var saved = _index;
        Trivia();
        if (Keyword() == keyword) return true;
        _index = saved;
        return false;
    }

    private bool StartsOperator(string text) => source.AsSpan(_index).StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
        (!char.IsAsciiLetter(text[^1]) || _index + text.Length == source.Length ||
            !(char.IsAsciiLetterOrDigit(source[_index + text.Length]) || source[_index + text.Length] == '_'));

    private bool AtExpressionEnd() => _index >= source.Length || ";\r\n|&)}]#".Contains(source[_index]);

    private void PowerShellVariable()
    {
        if (_index >= source.Length || source[_index++] != '$') throw Unsupported("expected a scalar PowerShell variable");
        var braced = _index < source.Length && source[_index] == '{';
        if (braced) _index++;
        var start = _index;
        while (_index < source.Length && (char.IsAsciiLetterOrDigit(source[_index]) || source[_index] is '_' or ':')) _index++;
        var name = source[start.._index];
        var parts = name.Split(':');
        if (parts.Length > 2 || parts.Any(part => !Identifier(part)) ||
            (parts.Length == 2 && !new[] { "env", "local", "script", "global", "private" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase)))
            throw Unsupported("only simple scalar/environment variables are classified");
        if (braced && (_index >= source.Length || source[_index++] != '}')) throw Unsupported("invalid braced variable");
    }
}
