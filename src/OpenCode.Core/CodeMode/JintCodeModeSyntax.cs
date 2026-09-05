namespace OpenCode.Core.CodeMode;

using Acornima;
using Acornima.Ast;
using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>A deliberately smaller, fail-closed subset of the source interpreter; see README.</summary>
internal static class JintCodeModeSyntax
{
    private static readonly HashSet<string> GlobalNames = ("tools search undefined NaN Infinity Object Array Math JSON Number String Boolean parseInt parseFloat isFinite isNaN " +
        "encodeURI encodeURIComponent decodeURI decodeURIComponent Promise Symbol Map Set Date RegExp URL URLSearchParams Error TypeError RangeError SyntaxError ReferenceError EvalError URIError AggregateError console")
        .Split(' ').ToHashSet(StringComparer.Ordinal);
    private sealed class PatternCodeScope(string name, int bodyStart = 0, bool generator = false, bool asynchronous = false)
    {
        internal readonly string Name = name;
        internal readonly int BodyStart = bodyStart;
        internal readonly bool Generator = generator;
        internal readonly bool Asynchronous = asynchronous;
        internal readonly List<string> Temporaries = [];
        internal bool Active;
    }

    internal static string Prepare(string source, string completed, string failed, CodeModeLimits limits, Action checkpoint)
    {
        var nodes = 0;
        var parser = new Parser(new ParserOptions
        {
            AllowReturnOutsideFunction = true,
            AllowAwaitOutsideFunction = true,
            AllowHashBang = false,
            OnToken = (in Token _) => checkpoint(),
            OnNode = (Node _, in OnNodeContext _) =>
            {
                checkpoint();
                if (++nodes > limits.MaxSyntaxNodes)
                    throw new CodeModeDiagnosticException(new("UnsupportedSyntax", "Program exceeds the syntax-node budget."));
            }
        });
        var program = parser.ParseScript(source);
        var wrappers = new Dictionary<Node, (string Helper, string? Plan)>();
        var patterns = new HashSet<Node>();
        var suspendedPatterns = new HashSet<Node>();
        var runtimeUnsupported = new Dictionary<Node, string>();
        var scopes = new Dictionary<Node, PatternCodeScope>();
        var parents = new Dictionary<Node, Node?>();
        var parameterBindings = new HashSet<Node>();
        var declarationBindings = new HashSet<Node>();
        var catchBindings = new HashSet<Node>();
        var ordinal = 0;
        var rootScope = new PatternCodeScope("__oc_scope0");
        var pending = new Stack<(Node Node, Node? Parent, PatternCodeScope Scope, int Depth)>();
        pending.Push((program, null, rootScope, 0));
        while (pending.TryPop(out var item))
        {
            checkpoint();
            var scope = item.Node is IFunction scopedFunction ? new PatternCodeScope("__oc_scope" + ++ordinal, scopedFunction.Body.Start, scopedFunction.Generator, scopedFunction.Async) : item.Scope;
            scopes[item.Node] = scope;
            parents[item.Node] = item.Parent;
            if (item.Depth > 128) Reject(item.Node, "Syntax nesting exceeds 128 levels.");
            if (item.Node is NewExpression creation && (creation.Callee is not Identifier constructor || !ConstructorName(constructor.Name)))
            {
                // Source evaluateNewExpression rejects these before evaluating
                // either callee or arguments. Never pass their subtree to Jint.
                runtimeUnsupported[item.Node] = "NewExpression";
                continue;
            }
            if (item.Node is Property unsupportedProperty && unsupportedProperty.Kind.ToString() != "Init")
            {
                runtimeUnsupported[item.Node] = "Property";
                continue;
            }
            if (item.Node is Literal { Kind: TokenKind.BigIntLiteral })
            {
                runtimeUnsupported[item.Node] = "BigIntLiteral";
                continue;
            }
            if (item.Node.Type is NodeType.ClassDeclaration or NodeType.ClassExpression or NodeType.TaggedTemplateExpression or
                NodeType.ThisExpression or NodeType.WithStatement or NodeType.DebuggerStatement or NodeType.ImportExpression)
            {
                runtimeUnsupported[item.Node] = item.Node.TypeText;
                continue;
            }
            Validate(item.Node);
            if (item.Node is YieldExpression && !scope.Generator) Reject(item.Node, "yield requires a generator invocation.");
            if (item.Node is AssignmentExpression assignment && OperatorToken(assignment.Left, assignment.Right) is not
                ("=" or "+=" or "-=" or "*=" or "/=" or "%=" or "**=" or "&=" or "|=" or "^=" or "<<=" or ">>=" or ">>>=" or "&&=" or "||=" or "??="))
                Reject(item.Node, "Unsupported assignment operator.");
            if (item.Node is IFunction bindingFunction && bindingFunction.Params.Any(HasBindingPattern))
            {
                parameterBindings.Add(item.Node);
                foreach (var parameter in bindingFunction.Params) MarkBinding(parameter, scope);
            }
            if (item.Node is VariableDeclaration declaration)
            {
                if (DeclarationKind(declaration) == "var") declarationBindings.Add(item.Node);
                if (DeclarationKind(declaration) == "var" && item.Parent is ForStatement header && ReferenceEquals(header.Init, item.Node)) scope.Active = true;
                foreach (var declarator in declaration.Declarations)
                {
                    if (declarator.Id is Identifier) continue;
                    declarationBindings.Add(item.Node);
                    MarkBinding(declarator.Id, scope);
                }
            }
            if (item.Node is CatchClause { Param: { } caught })
            {
                catchBindings.Add(item.Node);
                MarkBinding(caught, scope);
            }
            if (item.Node is AssignmentExpression { Left: ObjectPattern or ArrayPattern } destructuring)
            {
                if (OperatorToken(destructuring.Left, destructuring.Right) != "=") Reject(item.Node, "Destructuring requires plain assignment.");
                MarkAssignment(destructuring.Left);
                if (FreeSuspension(destructuring.Left))
                {
                    suspendedPatterns.Add(destructuring);
                    scope.Active = true;
                }
            }
            if (item.Node is ForOfStatement { Left: ObjectPattern or ArrayPattern } assignedLoop)
            {
                MarkAssignment(assignedLoop.Left);
                if (FreeSuspension(assignedLoop.Left)) { suspendedPatterns.Add(assignedLoop); scope.Active = true; }
            }
            if (item.Node is Property { Computed: true } && item.Parent is not ObjectExpression && !patterns.Contains(item.Parent!))
                Reject(item.Node, "Computed binding keys are not supported in this position.");
            if (item.Node is ObjectPattern && !patterns.Contains(item.Node))
                Reject(item.Node, "Object patterns are supported in initialized declarations, not parameters, loop bindings, or assignments.");
            if (item.Node is SpreadElement spread)
            {
                if (item.Parent is not (ObjectExpression or ArrayExpression or CallExpression or NewExpression))
                    Reject(spread, "Unsupported spread position.");
                wrappers[spread.Argument] = (item.Parent is ObjectExpression ? "__oc_objectSpread" : "__oc_iterableSpread", null);
            }
            foreach (var child in item.Node.ChildNodes) pending.Push((child, item.Node, scope, item.Depth + 1));
        }
        var lexical = new CodeModeLexical(program, parents, runtimeUnsupported);
        // Use parsed ranges: strings, comments, ASI, and nested function returns are
        // never guessed by string splitting. Jint parses only this validated source
        // plus a fixed host wrapper, with guest string compilation disabled.
        var last = program.Body.Count > 0 ? program.Body[^1] as ExpressionStatement : null;
        var body = Render(program);
        return "(async () => {\n" + CellPrelude(lexical.Root) + ScopeStatements(rootScope, body) + "\n})().then(" + completed + ", " + failed + ");";

        string Render(Node node)
        {
            checkpoint();
            if (ReferenceEquals(node, last)) return "return (" + Render(last!.Expression) + ");";
            var text = new StringBuilder();
            if (runtimeUnsupported.TryGetValue(node, out var unsupported))
            {
                if (unsupported == "BigIntLiteral") text.Append("__oc_invalidLiteral()");
                else text.Append(unsupported == "Property" ? "..." : "").Append("__oc_unsupported(").Append(JsonSerializer.Serialize(unsupported)).Append(')');
                if (node.Type is NodeType.ClassDeclaration or NodeType.WithStatement or NodeType.DebuggerStatement) text.Append(';');
            }
            else if (node is FunctionDeclaration ignored && lexical.IgnoredFunctions.Contains(ignored)) text.Append(';');
            else if (node is Identifier reference && lexical.IsReference(reference) && NeedsCell(reference)) text.Append(ReadIdentifier(reference));
            else if (node is UnaryExpression { Argument: Identifier typed } typeOf && SourceToken(typeOf.Start, typed.Start) == "typeof" && NeedsCell(typed))
                text.Append(ReadIdentifier(typed, type: true));
            else if (node is IFunction { Generator: true } generator) text.Append(GeneratorFunction(generator, node is FunctionDeclaration));
            else if (node is IFunction { Async: true } asyncFunction) text.Append(AsyncFunction(asyncFunction, node));
            else if (node is IFunction boundFunction && parameterBindings.Contains(node)) text.Append(FunctionWithBindings(boundFunction, node));
            else if (node is FunctionExpression { Id: not null } named)
                text.Append((named.Async ? "async " : "") + "function(" + string.Join(",", named.Params.Select(Render)) + ")" +
                    (scopes[node].Active ? ScopeBody(named.Body) : Render(named.Body)));
            else if (node is IFunction functionScope && scopes[node].Active)
                text.Append(Original(node, child => ReferenceEquals(child, functionScope.Body) ? ScopeBody(functionScope.Body) : Render(child)));
            else if (node is BlockStatement && (scopes[node].Active || lexical.Owned[node].HasCells))
            {
                var block = Original(node);
                text.Append('{').Append(CellPrelude(lexical.Owned[node]));
                if (scopes[node].Active) text.Append("try{").Append(block[1..^1]).Append("}catch(__oc_patternError){__oc_patternUnwind(")
                    .Append(scopes[node].Name).Append(");throw __oc_patternError;}");
                else text.Append(block[1..^1]);
                text.Append('}');
            }
            else if (node is SwitchStatement && lexical.Owned[node].HasCells)
                text.Append('{').Append(CellPrelude(lexical.Owned[node])).Append(Original(node)).Append('}');
            else if (node is TryStatement attempt && scopes[node].Active)
            {
                text.Append(Original(node, child =>
                {
                    if (!ReferenceEquals(child, attempt.Finalizer)) return Render(child);
                    var block = Render(child);
                    return "{__oc_patternUnwind(" + scopes[node].Name + ");" + block[1..^1] + "}";
                }));
                if (attempt.Finalizer is null) text.Append("finally{__oc_patternUnwind(").Append(scopes[node].Name).Append(");}");
            }
            else if (node is LabeledStatement labeled && BindingLoop(labeled) is { } labeledLoop)
                text.Append(RenderBindingLoop(labeledLoop.Loop, labeledLoop.Labels));
            else if (node is ForOfStatement { Left: VariableDeclaration } || node is ForInStatement)
                text.Append(RenderBindingLoop(node, ""));
            else if (node is ForStatement classic && lexical.ClassicLoops[classic].HasCells)
                text.Append(RenderClassicFor(classic, ""));
            else if (node is ForOfStatement iteration && (scopes[node].Active || iteration.Left is MemberExpression or ObjectPattern or ArrayPattern || iteration.Left is Identifier assignedName && NeedsCell(assignedName)))
                text.Append(RenderForOf(iteration));
            else if (node is VariableDeclaration declared && declarationBindings.Contains(node))
                text.Append(RenderDeclaration(declared));
            else if (node is CatchClause caught && catchBindings.Contains(node))
                text.Append(RenderCatch(caught));
            else if (node is NewExpression creation && creation.Callee is Identifier constructor)
            {
                text.Append("__oc_new" + constructor.Name + "(");
                text.Append(string.Join(",", creation.Arguments.Select(Argument)));
                text.Append(')');
            }
            else if (node is Literal { Kind: TokenKind.RegExpLiteral } regex)
            {
                // Delimiters come from a parser-validated regexp token, not a
                // regex search over JavaScript source. Flags cannot contain '/'.
                var end = regex.Raw.LastIndexOf('/');
                text.Append("__oc_newRegExp(").Append(JsonSerializer.Serialize(regex.Raw[1..end])).Append(',')
                    .Append(JsonSerializer.Serialize(regex.Raw[(end + 1)..])).Append(')');
            }
            else if (node is AssignmentExpression { Left: ObjectPattern or ArrayPattern } destructuring)
            {
                if (suspendedPatterns.Contains(node)) text.Append(SuspendedAssignment(destructuring));
                else text.Append("__oc_patternAssign((").Append(Render(destructuring.Right)).Append("),").Append(AssignmentPlan(destructuring.Left)).Append(')');
            }
            else if (node is ObjectProperty { Method: true, Value: IFunction { Generator: true } methodGenerator } generatorProperty)
            {
                var value = GeneratorFunction(methodGenerator, false);
                if (generatorProperty.Computed || BlockedStaticKey(generatorProperty)) text.Append("...__oc_property((").Append(PropertyKey(generatorProperty)).Append("))(").Append(value).Append(')');
                else text.Append(generatorProperty.Key is Identifier name ? JsonSerializer.Serialize(name.Name) : Render(generatorProperty.Key)).Append(':').Append(value);
            }
            else if (node is ObjectProperty { Method: true, Value: IFunction { Async: true } asyncMethod } asyncProperty)
            {
                var value = AsyncFunction(asyncMethod, asyncProperty.Value);
                if (asyncProperty.Computed || BlockedStaticKey(asyncProperty)) text.Append("...__oc_property((").Append(PropertyKey(asyncProperty)).Append("))(").Append(value).Append(')');
                else text.Append(PropertyKey(asyncProperty)).Append(':').Append(value);
            }
            else if (node is Property property && (property.Computed || BlockedStaticKey(property)))
            {
                var value = property is ObjectProperty { Method: true } && property.Value is IFunction function
                    ? parameterBindings.Contains(property.Value) ? FunctionWithBindings(function, property.Value) :
                        (function.Async ? "async " : "") + "function(" + string.Join(",", function.Params.Select(Render)) + ")" +
                        (scopes[function.Body].Active ? ScopeBody(function.Body) : Render(function.Body))
                    : Render(property.Value);
                text.Append("...__oc_property((").Append(PropertyKey(property)).Append("))((").Append(value).Append("))");
            }
            else if (node is ObjectProperty { Method: true, Value: IFunction method } plainMethod && parameterBindings.Contains(plainMethod.Value))
                text.Append(PropertyKey(plainMethod)).Append(':').Append(FunctionWithBindings(method, plainMethod.Value));
            else if (node is ObjectProperty { Shorthand: true, Value: Identifier shorthand } && NeedsCell(shorthand))
                text.Append(JsonSerializer.Serialize(shorthand.Name)).Append(':').Append(ReadIdentifier(shorthand));
            else if (node is YieldExpression { Delegate: true } delegated)
                text.Append("__oc_unboxYield((yield* __oc_delegate((").Append(delegated.Argument is null ? "void 0" : Render(delegated.Argument))
                    .Append("),").Append(scopes[node].Asynchronous ? "true" : "false").Append(")))");
            else if (node is AwaitExpression awaited)
                text.Append("await __oc_observe((").Append(Render(awaited.Argument)).Append("))");
            else if (node is ThrowStatement thrown)
                text.Append("throw __oc_thrown((").Append(Render(thrown.Argument)).Append("));");
            else if (node is AssignmentExpression { Left: MemberExpression member } assignment)
            {
                var operation = OperatorToken(assignment.Left, assignment.Right);
                if (operation is "&&=" or "||=" or "??=")
                    text.Append("(__oc_logicalRef(").Append(MemberArguments(member)).Append(").value ").Append(operation)
                        .Append(" (").Append(Render(assignment.Right)).Append("))");
                else
                    text.Append("__oc_assign(").Append(MemberArguments(member)).Append(',').Append(JsonSerializer.Serialize(operation))
                        .Append(")((").Append(Render(assignment.Right)).Append("))");
            }
            else if (node is AssignmentExpression { Left: Identifier cellTarget } cellAssignment && NeedsCell(cellTarget))
            {
                var operation = OperatorToken(cellAssignment.Left, cellAssignment.Right);
                if (operation == "=") text.Append(WriteIdentifier(cellTarget, Render(cellAssignment.Right)));
                else if (operation is "&&=" or "||=" or "??=")
                    text.Append("(__oc_logicalCell(").Append(CellAccess(cellTarget)).Append(").value ").Append(operation).Append(" (").Append(Render(cellAssignment.Right)).Append("))");
                else text.Append("__oc_compoundCell(").Append(CellAccess(cellTarget)).Append(',').Append(JsonSerializer.Serialize(operation)).Append(")((").Append(Render(cellAssignment.Right)).Append("))");
            }
            else if (node is AssignmentExpression { Left: Identifier identifier } assigned && OperatorToken(assigned.Left, assigned.Right) is not ("=" or "&&=" or "||=" or "??="))
                text.Append('(').Append(identifier.Name).Append("=__oc_compound(").Append(JsonSerializer.Serialize(OperatorToken(assigned.Left, assigned.Right)))
                    .Append(',').Append(identifier.Name).Append(",(").Append(Render(assigned.Right)).Append(")))");
            else if (node is UpdateExpression update)
            {
                var token = update.Prefix ? SourceToken(update.Start, update.Argument.Start) : SourceToken(update.Argument.End, update.End);
                var increment = token == "++" ? "1" : token == "--" ? "-1" : throw new CodeModeDiagnosticException(new("UnsupportedSyntax", "Unsupported update operator."));
                if (update.Argument is MemberExpression updatedMember)
                    text.Append("__oc_updateMember(").Append(MemberArguments(updatedMember)).Append(',').Append(increment).Append(',')
                        .Append(update.Prefix ? "true" : "false").Append(')');
                else
                {
                    if (update.Argument is Identifier cell && NeedsCell(cell))
                        text.Append("__oc_updateCell(").Append(CellAccess(cell)).Append(',').Append(increment).Append(',').Append(update.Prefix ? "true" : "false").Append(')');
                    else
                    {
                        var name = ((Identifier)update.Argument).Name;
                        // A synchronous lexical wrapper preserves postfix's old numeric
                        // result without introducing an await or a shared temporary.
                        text.Append("((__oc_updatePacket)=>(").Append(name).Append("=__oc_updatePacket.next,")
                            .Append(update.Prefix ? "__oc_updatePacket.next" : "__oc_updatePacket.previous")
                            .Append("))(__oc_updateValue(").Append(name).Append(',').Append(increment).Append("))");
                    }
                }
            }
            else if (node is UnaryExpression unary && SourceToken(unary.Start, unary.Argument.Start) == "delete")
            {
                var deletion = (unary.Argument is ChainExpression chain ? chain.Expression : unary.Argument) as MemberExpression
                    ?? throw new CodeModeDiagnosticException(new("UnsupportedSyntax", "Only member deletion is supported."));
                var members = new List<Node>();
                Node root = deletion;
                while (root is MemberExpression or CallExpression)
                {
                    members.Add(root);
                    root = root is MemberExpression part ? part.Object : ((CallExpression)root).Callee;
                }
                members.Reverse();
                bool Optional(Node part) => part is MemberExpression access ? access.Optional : ((CallExpression)part).Optional;
                text.Append("(__oc_deleteChain((").Append(Render(root)).Append("),").Append(Optional(members[0]) ? "true" : "false").Append(')');
                for (var index = 0; index < members.Count; index++)
                {
                    if (members[index] is CallExpression invocation)
                    {
                        text.Append("?.call([").Append(string.Join(",", invocation.Arguments.Select(Argument))).Append(']');
                    }
                    else
                    {
                        var part = (MemberExpression)members[index];
                        text.Append(index == members.Count - 1 ? "?.remove(" : "?.get(");
                        text.Append(part.Computed ? "(" + Render(part.Property) + ")" : JsonSerializer.Serialize(((Identifier)part.Property).Name));
                    }
                    if (index < members.Count - 1) text.Append(',').Append(Optional(members[index + 1]) ? "true" : "false");
                    text.Append(')');
                }
                text.Append("??true)");
            }
            else if (node is BinaryExpression binary && OperatorToken(binary.Left, binary.Right) == "instanceof")
                text.Append("__oc_instanceof((").Append(Render(binary.Left)).Append("),(").Append(Render(binary.Right)).Append("))");
            else
                text.Append(Original(node));
            if (!wrappers.TryGetValue(node, out var wrapper)) return text.ToString();
            return wrapper.Helper + "((" + text + ")" + (wrapper.Plan is null ? "" : "," + JsonSerializer.Serialize(wrapper.Plan)) + ")";
        }

        // Inserted call arguments must stay single expressions: a source comma
        // expression is not a second helper argument. Spread remains syntax.
        string Argument(Node node) => node is SpreadElement ? Render(node) : "(" + Render(node) + ")";

        string MemberArguments(MemberExpression member) => "(" + Render(member.Object) + ")," +
            (member.Computed ? "(" + Render(member.Property) + ")" : JsonSerializer.Serialize(((Identifier)member.Property).Name));

        (List<CodeModeLexical.Scope> Chain, bool Missing) Lookup(Identifier id)
        {
            var chain = new List<CodeModeLexical.Scope>();
            for (var current = lexical.At[id]; current is not null; current = current.Parent)
            {
                if (current.Native.Contains(id.Name)) return (chain, false);
                if (current.Progressive.Contains(id.Name)) chain.Add(current);
            }
            return (chain, !GlobalNames.Contains(id.Name));
        }

        bool NeedsCell(Identifier id) { var lookup = Lookup(id); return lookup.Missing || lookup.Chain.Count > 0; }
        string ChainText(List<CodeModeLexical.Scope> chain) => "[" + string.Join(",", chain.Select(scope => scope.Name)) + "]";
        string ReadIdentifier(Identifier id, bool type = false)
        {
            var lookup = Lookup(id);
            if (lookup.Chain.Count == 0 && !lookup.Missing) return type ? "typeof " + id.Name : id.Name;
            return (type ? "__oc_typeCell(" : "__oc_readCell(") + ChainText(lookup.Chain) + "," + JsonSerializer.Serialize(id.Name) +
                ",()=>" + (lookup.Missing ? "void 0" : type ? "typeof " + id.Name : id.Name) + "," + (lookup.Missing ? "true" : "false") + ")";
        }

        string CellAccess(Identifier id)
        {
            var lookup = Lookup(id);
            return ChainText(lookup.Chain) + "," + JsonSerializer.Serialize(id.Name) + ",()=>" + (lookup.Missing ? "void 0" : id.Name) +
                ",(__oc_cellValue)=>" + (lookup.Missing ? "void 0" : "(" + id.Name + "=__oc_cellValue)") + "," + (lookup.Missing ? "true" : "false");
        }

        string WriteIdentifier(Identifier id, string value)
        {
            var lookup = Lookup(id);
            if (lookup.Chain.Count == 0 && !lookup.Missing) return "(" + id.Name + "=(" + value + "))";
            return "__oc_writeCell(" + ChainText(lookup.Chain) + "," + JsonSerializer.Serialize(id.Name) + ",(" + value +
                "),(__oc_cellValue)=>" + (lookup.Missing ? "void 0" : "(" + id.Name + "=__oc_cellValue)") + "," + (lookup.Missing ? "true" : "false") + ")";
        }

        string CellInitial(CodeModeLexical.Scope scope) => "__oc_cells(" + JsonSerializer.Serialize(scope.Native.OrderBy(name => name, StringComparer.Ordinal).ToArray()) + ")";
        string CellPrelude(CodeModeLexical.Scope scope) => scope.HasCells ? "const " + scope.Name + "=" + CellInitial(scope) + ";" : "";

        string GeneratorFunction(IFunction function, bool declaration)
        {
            var name = declaration && function.Id is not null ? " " + function.Id.Name : "";
            if (declaration && name.Length == 0) Reject(function.Body, "Generator declaration requires a name.");
            var lower = function.Params.Any(HasBindingPattern);
            var body = lower ? ParameterBody(function, "__oc_generatorArgs") : scopes[function.Body].Active ? ScopeBody(function.Body) : Render(function.Body);
            // Calling this ordinary wrapper only captures arguments. The inner
            // native continuation (including parameter initialization) is created
            // lazily by the handle's first next request, never by return/throw first.
            return "function" + name + "(...__oc_generatorArgs){return __oc_generator(()=>(" +
                (function.Async ? "async " : "") + "function*(" + (lower ? "" : string.Join(",", function.Params.Select(Render))) + ")" + body +
                ")(" + (lower ? "" : "...__oc_generatorArgs") + ")," + (function.Async ? "true" : "false") + ");}";
        }

        string FunctionWithBindings(IFunction function, Node node)
        {
            var prefix = function.Async ? "async " : "";
            var body = ParameterBody(function, "__oc_callArgs");
            return node is ArrowFunctionExpression ? prefix + "(...__oc_callArgs)=>" + body :
                prefix + "function" + (node is FunctionDeclaration && function.Id is not null ? " " + function.Id.Name : "") + "(...__oc_callArgs)" + body;
        }

        string AsyncFunction(IFunction function, Node node)
        {
            var lower = function.Params.Any(HasBindingPattern);
            var body = lower ? ParameterBody(function, "__oc_callArgs") : function.Body is BlockStatement
                ? scopes[function.Body].Active ? ScopeBody(function.Body) : Render(function.Body)
                : "{" + ScopeStatements(scopes[function.Body], "return (" + Render(function.Body) + ");") + "}";
            var invocation = "__oc_asyncCall(()=>(async function(" + (lower ? "...__oc_callArgs" : string.Join(",", function.Params.Select(Render))) +
                ")" + body + ")(...__oc_asyncArgs))";
            return node is ArrowFunctionExpression ? "(...__oc_asyncArgs)=>" + invocation :
                "function" + (node is FunctionDeclaration && function.Id is not null ? " " + function.Id.Name : "") +
                "(...__oc_asyncArgs){return " + invocation + ";}";
        }

        string ParameterBody(IFunction function, string arguments)
        {
            var scope = scopes[function.Body];
            var declarations = new List<string>();
            var index = 0;
            foreach (var parameter in function.Params)
            {
                var value = parameter is RestElement ? "__oc_argumentsTail(" + arguments + "," + index + ")" : arguments + "[" + index + "]";
                BindingParts(parameter is RestElement rest ? rest.Argument : parameter, value, scope, declarations);
                index++;
            }
            // One native lexical declaration reserves all parameter names before
            // the first initializer. The original body gets its own nested scope.
            var content = "let " + string.Join(",", declarations) + ";" +
                (function.Body is BlockStatement ? Render(function.Body) : "return (" + Render(function.Body) + ");");
            return "{" + ScopeStatements(scope, content) + "}";
        }

        string RenderDeclaration(VariableDeclaration declaration)
        {
            var header = parents[declaration] is ForStatement loop && ReferenceEquals(loop.Init, declaration);
            var parts = new List<string>();
            var progressive = DeclarationKind(declaration) == "var" ? lexical.At[declaration] : null;
            foreach (var item in declaration.Declarations)
                BindingParts(item.Id, item.Init is null ? "void 0" : Render(item.Init), scopes[declaration], parts, progressive);
            if (progressive is not null)
            {
                if (header)
                {
                    foreach (var part in parts) scopes[declaration].Temporaries.Add(part[..part.IndexOf('=')]);
                    return "(" + string.Join(",", parts) + ")";
                }
                var body = "const " + string.Join(",", parts) + ";";
                if (scopes[declaration].Active) body = "try{" + body + "}catch(__oc_declarationError){__oc_patternUnwind(" + scopes[declaration].Name + ");throw __oc_declarationError;}";
                return "{" + body + "}";
            }
            return DeclarationKind(declaration) + " " + string.Join(",", parts) + (header ? "" : ";");
        }

        string RenderCatch(CatchClause clause)
        {
            var input = "__oc_caughtValue" + ++ordinal;
            var scope = scopes[clause];
            var declarations = new List<string>();
            BindingParts(clause.Param!, "__oc_caught(" + input + ")", scope, declarations, lexical.Owned[clause]);
            var body = "const " + string.Join(",", declarations) + ";" + Render(clause.Body);
            if (scope.Active) body = "try{" + body + "}catch(__oc_bindingError){__oc_patternUnwind(" + scope.Name + ");throw __oc_bindingError;}";
            return "catch(" + input + "){" + CellPrelude(lexical.Owned[clause]) + body + "}";
        }

        void BindingParts(Node pattern, string input, PatternCodeScope scope, List<string> parts, CodeModeLexical.Scope? progressive = null)
        {
            checkpoint();
            string Hold(string value)
            {
                var name = "__oc_binding" + ++ordinal;
                parts.Add(name + "=(" + value + ")");
                return name;
            }
            switch (pattern)
            {
                case Identifier id:
                    if (progressive is null) parts.Add(id.Name + "=(" + input + ")");
                    else Hold("__oc_declareCell(" + progressive.Name + "," + JsonSerializer.Serialize(id.Name) + ",(" + input + "))");
                    return;
                case AssignmentPattern fallback:
                    var initial = Hold(input);
                    BindingParts(fallback.Left, initial + "===void 0?(" + Render(fallback.Right) + "):" + initial, scope, parts, progressive);
                    return;
                case ObjectPattern obj:
                    var record = Hold("__oc_patternObject((" + input + "))");
                    foreach (var child in obj.ChildNodes)
                    {
                        if (child is RestElement rest) BindingParts(rest.Argument, "__oc_patternRest(" + record + ")", scope, parts, progressive);
                        else
                        {
                            var property = (Property)child;
                            BindingParts(property.Value, "__oc_patternRead(" + record + ",(" + PropertyKey(property) + "))", scope, parts, progressive);
                        }
                    }
                    return;
                case ArrayPattern array:
                    var cursor = Hold("__oc_patternArray(" + scope.Name + ",(" + input + "))");
                    foreach (var child in array.Elements)
                    {
                        if (child is null) { Hold("__oc_patternNext(" + cursor + ")"); continue; }
                        if (child is RestElement rest) BindingParts(rest.Argument, "__oc_patternTail(" + cursor + ")", scope, parts, progressive);
                        else BindingParts(child, "__oc_patternNext(" + cursor + ")", scope, parts, progressive);
                    }
                    Hold("__oc_patternFinish(" + cursor + ")");
                    return;
                default:
                    Reject(pattern, "Unsupported declaration binding pattern.");
                    return;
            }
        }

        void MarkBinding(Node node, PatternCodeScope scope)
        {
            MarkAssignment(node);
            foreach (var part in BindingNodes(node)) if (part is ArrayPattern) scope.Active = true;
        }

        bool HasBindingPattern(Node node) => BindingNodes(node).Any(part => part is ArrayPattern or ObjectPattern);
        string[] BindingNames(Node node) => BindingNodes(node).OfType<Identifier>().Select(id => id.Name).ToArray();

        IEnumerable<Node> BindingNodes(Node node)
        {
            yield return node;
            var children = node switch
            {
                AssignmentPattern fallback => new[] { fallback.Left },
                RestElement rest => new[] { rest.Argument },
                Property property => new[] { property.Value },
                ArrayPattern or ObjectPattern => node.ChildNodes.ToArray(),
                _ => Array.Empty<Node>()
            };
            foreach (var child in children) foreach (var part in BindingNodes(child)) yield return part;
        }

        string DeclarationKind(VariableDeclaration declaration) => declaration.Kind.ToString().ToLowerInvariant();
        string PropertyKey(Property property) => property.Computed ? Render(property.Key) : property.Key is Identifier key ? JsonSerializer.Serialize(key.Name) : Render(property.Key);
        bool BlockedStaticKey(Property property) => !property.Computed &&
            (property.Key is Identifier id ? id.Name : property.Key is Literal literal ? literal.Value as string : null) is "constructor" or "prototype" or "__proto__";

        (Node Loop, string Labels)? BindingLoop(LabeledStatement statement)
        {
            var labels = new StringBuilder();
            Node body = statement;
            while (body is LabeledStatement label) { labels.Append(label.Label.Name).Append(':'); body = label.Body; }
            return body is ForInStatement || body is ForOfStatement { Left: VariableDeclaration } || body is ForStatement classic && lexical.ClassicLoops[classic].HasCells
                ? (body, labels.ToString()) : null;
        }

        string RenderBindingLoop(Node node, string labels)
        {
            if (node is ForStatement classic) return RenderClassicFor(classic, labels);
            var scope = scopes[node];
            var left = node is ForOfStatement each ? each.Left : ((ForInStatement)node).Left;
            var right = node is ForOfStatement eachSource ? eachSource.Right : ((ForInStatement)node).Right;
            var body = node is ForOfStatement eachBody ? eachBody.Body : ((ForInStatement)node).Body;
            var item = "__oc_iteration" + ++ordinal;
            var prefix = "";
            string[] shadows = [];
            if (left is VariableDeclaration declaration)
            {
                if (declaration.Declarations.Count != 1) Reject(declaration, "Loop binding requires one declaration.");
                var pattern = declaration.Declarations[0].Id;
                var names = BindingNames(pattern);
                var kind = DeclarationKind(declaration);
                var parts = new List<string>();
                BindingParts(pattern, item, scope, parts, kind == "var" ? lexical.Iterations[node] : null);
                prefix = (kind == "var" || kind == "const" ? "const " : "let ") + string.Join(",", parts) + ";";
                if (kind != "var") shadows = names.Distinct(StringComparer.Ordinal).ToArray();
            }
            else prefix = WriteIdentifier((Identifier)left, item) + ";";
            var statements = prefix + Render(body);
            if (scope.Active) statements = "try{" + statements + "}catch(__oc_bindingError){__oc_patternUnwind(" + scope.Name + ");throw __oc_bindingError;}";
            if (lexical.Iterations.TryGetValue(node, out var iteration)) statements = CellPrelude(iteration) + statements;
            var input = node is ForInStatement ? "__oc_forInKeys((" + Render(right) + "))" : "(" + Render(right) + ")";
            var loop = labels + "for" + (node is ForOfStatement { Await: true } ? " await" : "") + "(const " + item + " of " + input + "){" + statements + "}";
            if (shadows.Length == 0) return loop;
            var boundary = "__oc_bindingScope" + ++ordinal;
            // Never initialize the RHS-only TDZ cells. A closure created while
            // evaluating the iterable must retain that uninitialized environment.
            return boundary + ":{" + loop + "break " + boundary + ";let " + string.Join(",", shadows) + ";}";
        }

        string RenderClassicFor(ForStatement loop, string labels)
        {
            var environment = lexical.ClassicLoops[loop];
            var test = loop.Test is null ? "true" : Render(loop.Test);
            var update = loop.Update is null ? "void 0" : Render(loop.Update);
            var body = Render(loop.Body);
            if (loop.Init is VariableDeclaration declaration && DeclarationKind(declaration) is "let" or "const")
            {
                var parts = new List<string>();
                foreach (var variable in declaration.Declarations)
                    BindingParts(variable.Id, variable.Init is null ? "void 0" : Render(variable.Init), scopes[loop], parts);
                var reset = environment.Name + "=" + CellInitial(environment);
                var head = DeclarationKind(declaration) == "let" ? "let " + reset + "," + string.Join(",", parts) : "let " + reset;
                var initial = DeclarationKind(declaration) == "const" ? "const " + string.Join(",", parts) + ";" : "";
                // Native let cells copy before update; resetting the hidden map
                // drops body vars like source nextIteration. Initializer closures
                // retain their initial cell, while iteration closures retain theirs.
                // Const user bindings can be shared: their value cannot change.
                return "{" + initial + labels + "for(" + head + ";(" + reset + ",(" + test + "));(" + reset + ",(" + update + "))){" + body + "}}";
            }
            var init = loop.Init is null ? "" : Render(loop.Init);
            return "{" + CellPrelude(environment) + labels + "for(" + init + ";(" + test + ");(" + update + ")){" + body + "}}";
        }

        string Original(Node node, Func<Node, string>? render = null)
        {
            var text = new StringBuilder();
            var position = node.Start;
            foreach (var child in node.ChildNodes)
            {
                if (child.Start < position) continue;
                text.Append(source, position, child.Start - position);
                text.Append(render is null ? Render(child) : render(child));
                position = child.End;
            }
            return text.Append(source, position, node.End - position).ToString();
        }

        string ScopeBody(Node body)
        {
            var content = body is BlockStatement ? Original(body)[1..^1] : "return (" + Render(body) + ");";
            return "{" + (lexical.Owned.TryGetValue(body, out var owned) ? CellPrelude(owned) : "") + ScopeStatements(scopes[body], content) + "}";
        }

        string ScopeStatements(PatternCodeScope scope, string content)
        {
            if (!scope.Active) return content;
            return "const " + scope.Name + "=__oc_patternScope();" +
                (scope.Temporaries.Count == 0 ? "" : "let " + string.Join(",", scope.Temporaries) + ";") +
                "try{" + content + "}catch(__oc_patternError){__oc_patternUnwind(" + scope.Name + ");throw __oc_patternError;}" +
                "finally{__oc_patternUnwind(" + scope.Name + ");}";
        }

        string SuspendedAssignment(AssignmentExpression assignment)
            => SuspendedPattern(assignment.Left, Render(assignment.Right), scopes[assignment]);

        string SuspendedPattern(Node assignmentPattern, string input, PatternCodeScope scope)
        {
            string Temporary()
            {
                var name = "__oc_hold" + ++ordinal;
                scope.Temporaries.Add(name);
                return name;
            }
            var root = Temporary();
            var sequence = new List<string> { root + "=(" + input + ")" };
            Emit(assignmentPattern, root);
            sequence.Add(root);
            return "(" + string.Join(",", sequence) + ")";

            void Emit(Node pattern, string value)
            {
                checkpoint();
                switch (pattern)
                {
                    case Identifier id:
                        sequence.Add(WriteIdentifier(id, value));
                        return;
                    case MemberExpression member:
                        sequence.Add("__oc_assign(" + MemberArguments(member) + ",'=')(" + value + ")");
                        return;
                    case AssignmentPattern fallback:
                        var resolved = Temporary();
                        sequence.Add(resolved + "=" + value);
                        // Keep the await in its original conditional branch. No
                        // promise wrapper or unconditional synthetic await is used.
                        sequence.Add(resolved + "===void 0?(" + resolved + "=(" + Render(fallback.Right) + ")):void 0");
                        Emit(fallback.Left, resolved);
                        return;
                    case ObjectPattern obj:
                        var record = Temporary();
                        sequence.Add(record + "=__oc_patternObject(" + value + ")");
                        foreach (var child in obj.ChildNodes)
                        {
                            var item = Temporary();
                            if (child is RestElement rest)
                            {
                                sequence.Add(item + "=__oc_patternRest(" + record + ")");
                                Emit(rest.Argument, item);
                                continue;
                            }
                            var property = (Property)child;
                            var key = property.Computed ? Render(property.Key) : property.Key is Identifier name ? JsonSerializer.Serialize(name.Name) : Render(property.Key);
                            sequence.Add(item + "=__oc_patternRead(" + record + ",(" + key + "))");
                            Emit(property.Value, item);
                        }
                        return;
                    case ArrayPattern array:
                        var cursor = Temporary();
                        sequence.Add(cursor + "=__oc_patternArray(" + scope.Name + "," + value + ")");
                        foreach (var child in array.Elements)
                        {
                            if (child is null) { sequence.Add("__oc_patternNext(" + cursor + ")"); continue; }
                            var item = Temporary();
                            if (child is RestElement rest)
                            {
                                sequence.Add(item + "=__oc_patternTail(" + cursor + ")");
                                Emit(rest.Argument, item);
                            }
                            else
                            {
                                sequence.Add(item + "=__oc_patternNext(" + cursor + ")");
                                Emit(child, item);
                            }
                        }
                        sequence.Add("__oc_patternFinish(" + cursor + ")");
                        return;
                    default:
                        Reject(pattern, "Unsupported suspended assignment pattern.");
                        return;
                }
            }
        }

        string RenderForOf(ForOfStatement loop)
        {
            var scope = scopes[loop];
            var rewrite = loop.Left is MemberExpression or ObjectPattern or ArrayPattern || loop.Left is Identifier candidate && NeedsCell(candidate);
            var item = "__oc_iteration" + ++ordinal;
            var before = loop.Left switch
            {
                MemberExpression member => "__oc_assign(" + MemberArguments(member) + ",'=')(" + item + ");",
                ObjectPattern or ArrayPattern => (suspendedPatterns.Contains(loop)
                    ? SuspendedPattern(loop.Left, item, scope)
                    : "__oc_patternAssign(" + item + "," + AssignmentPlan(loop.Left) + ")") + ";",
                Identifier id when NeedsCell(id) => WriteIdentifier(id, item) + ";",
                _ => ""
            };
            var body = before + Render(loop.Body);
            // Unwind inner pattern cursors BEFORE native for-of closes its outer
            // iterator. This also covers an expression (non-block) loop body.
            if (scope.Active) body = "try{" + body + "}catch(__oc_loopError){__oc_patternUnwind(" + scope.Name + ");throw __oc_loopError;}";
            return "for" + (loop.Await ? " await" : "") + "(" + (rewrite ? "const " + item : Render(loop.Left)) +
                " of (" + Render(loop.Right) + ")){" + body + "}";
        }

        void MarkAssignment(Node node, int depth = 0)
        {
            if (depth > 32) Reject(node, "Assignment pattern nesting exceeds 32 levels.");
            patterns.Add(node);
            switch (node)
            {
                case Identifier: break;
                case MemberExpression: break;
                case AssignmentPattern fallback: MarkAssignment(fallback.Left, depth + 1); break;
                case RestElement rest: MarkAssignment(rest.Argument, depth + 1); break;
                case Property property:
                    MarkAssignment(property.Value, depth + 1);
                    break;
                case ObjectPattern or ArrayPattern:
                    foreach (var child in node.ChildNodes) MarkAssignment(child, depth + 1);
                    break;
                default: Reject(node, "Unsupported assignment pattern."); break;
            }
        }

        bool FreeSuspension(Node node)
        {
            var work = new Stack<Node>(); work.Push(node);
            while (work.TryPop(out var item))
            {
                if (item is IFunction) continue;
                if (item is AwaitExpression or YieldExpression) return true;
                foreach (var child in item.ChildNodes) work.Push(child);
            }
            return false;
        }

        string AssignmentPlan(Node node)
        {
            checkpoint();
            return node switch
            {
                Identifier id => "{kind:'target',set:(__oc_patternValue)=>" + WriteIdentifier(id, "__oc_patternValue") + "}",
                MemberExpression member => "{kind:'target',set:(__oc_patternValue)=>__oc_assign(" + MemberArguments(member) + ",'=')(__oc_patternValue)}",
                AssignmentPattern fallback => "{kind:'default',value:()=>(" + Render(fallback.Right) + "),node:" + AssignmentPlan(fallback.Left) + "}",
                RestElement rest => "{kind:'rest',node:" + AssignmentPlan(rest.Argument) + "}",
                ArrayPattern array => "{kind:'array',entries:[" + string.Join(",", array.Elements.Select(item => item is null ? "null" : AssignmentPlan(item))) + "]}",
                ObjectPattern obj => "{kind:'object',entries:[" + string.Join(",", obj.ChildNodes.Select(AssignmentPlan)) + "]}",
                Property property => "{kind:'property',key:()=>(" + (property.Computed ? Render(property.Key) : property.Key is Identifier key ? JsonSerializer.Serialize(key.Name) : Render(property.Key)) + "),node:" + AssignmentPlan(property.Value) + "}",
                _ => throw new CodeModeDiagnosticException(new("UnsupportedSyntax", "Unsupported assignment pattern."))
            };
        }

        string OperatorToken(Node left, Node right) => SourceToken(left.End, right.Start);

        string SourceToken(int start, int end)
        {
            var token = new StringBuilder();
            // The parser has already restricted this span to one operator plus
            // trivia. Preserve comments without treating their contents as tokens.
            for (var index = start; index < end; index++)
            {
                if (char.IsWhiteSpace(source[index])) continue;
                if (source[index] == '/' && index + 1 < end && source[index + 1] == '*')
                {
                    index = source.IndexOf("*/", index + 2, StringComparison.Ordinal) + 1;
                    continue;
                }
                if (source[index] == '/' && index + 1 < end && source[index + 1] == '/')
                {
                    while (index + 1 < end && source[index + 1] is not ('\r' or '\n')) index++;
                    continue;
                }
                token.Append(source[index]);
            }
            return token.ToString().Trim('(', ')');
        }
    }

    private static void Validate(Node node)
    {
        switch (node.Type)
        {
            case NodeType.Program: case NodeType.ArrayExpression: case NodeType.ArrayPattern:
            case NodeType.ArrowFunctionExpression: case NodeType.AssignmentPattern:
            case NodeType.AwaitExpression: case NodeType.BlockStatement: case NodeType.BreakStatement:
            case NodeType.CallExpression: case NodeType.CatchClause: case NodeType.ChainExpression:
            case NodeType.ConditionalExpression: case NodeType.ContinueStatement: case NodeType.DoWhileStatement:
            case NodeType.EmptyStatement: case NodeType.ExpressionStatement: case NodeType.ForInStatement:
            case NodeType.ForOfStatement: case NodeType.ForStatement: case NodeType.FunctionDeclaration:
            case NodeType.FunctionExpression: case NodeType.IfStatement: case NodeType.LabeledStatement:
            case NodeType.LogicalExpression: case NodeType.MemberExpression: case NodeType.ObjectExpression:
            case NodeType.RestElement: case NodeType.ReturnStatement: case NodeType.ObjectPattern: case NodeType.SpreadElement:
            case NodeType.SequenceExpression: case NodeType.SwitchCase: case NodeType.SwitchStatement:
            case NodeType.TemplateElement: case NodeType.TemplateLiteral: case NodeType.ThrowStatement:
            case NodeType.TryStatement: case NodeType.VariableDeclaration: case NodeType.VariableDeclarator:
            case NodeType.WhileStatement: case NodeType.YieldExpression: break;
            case NodeType.Identifier:
                if (((Identifier)node).Name.StartsWith("__oc_", StringComparison.Ordinal)) Reject(node, "Reserved host binding.");
                break;
            case NodeType.Literal:
                break;
            case NodeType.Property:
                break;
            case NodeType.AssignmentExpression:
                if (((AssignmentExpression)node).Left is not Identifier and not MemberExpression and not ObjectPattern and not ArrayPattern) Reject(node, "Unsupported assignment target.");
                break;
            case NodeType.UpdateExpression:
                if (((UpdateExpression)node).Argument is not Identifier and not MemberExpression) Reject(node, "Update target must be an identifier or member.");
                break;
            case NodeType.UnaryExpression:
                if (((UnaryExpression)node).Operator.ToString() == "Delete" && ((UnaryExpression)node).Argument is not MemberExpression and not ChainExpression)
                    Reject(node, "Only data-member deletion is supported.");
                break;
            case NodeType.BinaryExpression:
                if (((BinaryExpression)node).Operator.ToString() == "In")
                    Reject(node, "The in operator is unavailable.");
                break;
            case NodeType.NewExpression:
                break;
            default: Reject(node, $"Syntax '{node.TypeText}' is not supported by this evaluator."); break;
        }
        if (node is ForInStatement forIn && forIn.Left is not VariableDeclaration and not Identifier)
            Reject(node, "Member mutation in a loop binding is unavailable.");
        if (node is ForOfStatement forOf && forOf.Left is not VariableDeclaration and not Identifier and not MemberExpression and not ObjectPattern and not ArrayPattern)
            Reject(node, "Unsupported for-of assignment target.");
    }

    private static bool ConstructorName(string name) => name is "Array" or "Object" or "Promise" or "Map" or "Set" or "Date" or "RegExp" or "URL" or "URLSearchParams" or
        "Error" or "TypeError" or "RangeError" or "SyntaxError" or "ReferenceError" or "EvalError" or "URIError" or "AggregateError";

    private static void Reject(Node node, string message) => throw new CodeModeDiagnosticException(new("UnsupportedSyntax", message,
        new(node.Location.Start.Line, node.Location.Start.Column + 1)));
}
