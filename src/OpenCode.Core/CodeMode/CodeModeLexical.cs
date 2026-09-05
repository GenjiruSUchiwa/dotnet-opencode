namespace OpenCode.Core.CodeMode;

using Acornima.Ast;

/// <summary>Source ScopeStack ownership, independent of native JavaScript var hoisting.</summary>
internal sealed class CodeModeLexical
{
    internal sealed class Scope(Node owner, Scope? parent, string name)
    {
        internal readonly Node Owner = owner;
        internal readonly Scope? Parent = parent;
        internal readonly string Name = name;
        internal readonly HashSet<string> Native = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Progressive = new(StringComparer.Ordinal);
        internal bool HasCells => Progressive.Count > 0;
    }

    internal readonly Dictionary<Node, Scope> At = [];
    internal readonly Dictionary<Node, Scope> Owned = [];
    internal readonly Dictionary<Node, Scope> Iterations = [];
    internal readonly Dictionary<Node, Scope> ClassicLoops = [];
    internal readonly HashSet<Identifier> Bindings = [];
    internal readonly HashSet<FunctionDeclaration> IgnoredFunctions = [];
    internal Scope Root { get; }
    private readonly IReadOnlyDictionary<Node, Node?> _parents;
    private readonly IReadOnlyDictionary<Node, string> _unsupported;
    private int _next;

    internal CodeModeLexical(Node root, IReadOnlyDictionary<Node, Node?> parents, IReadOnlyDictionary<Node, string> unsupported)
    {
        _parents = parents;
        _unsupported = unsupported;
        Root = New(root, null);
        Seed(Root, root.ChildNodes, functions: true);
        Visit(root, Root, rootOwned: true);
    }

    private Scope New(Node owner, Scope? parent) => new(owner, parent, "__oc_env" + _next++);

    private void Seed(Scope scope, IEnumerable<Node> statements, bool functions)
    {
        foreach (var statement in statements)
        {
            if (statement is VariableDeclaration declaration && declaration.Kind.ToString() != "Var")
                foreach (var item in declaration.Declarations) scope.Native.UnionWith(Names(item.Id));
            if (functions && statement is FunctionDeclaration { Id: { } id }) scope.Native.Add(id.Name);
        }
    }

    private void Visit(Node node, Scope scope, bool rootOwned = false)
    {
        At[node] = scope;
        if (_unsupported.ContainsKey(node)) return;
        if (node is FunctionDeclaration declaration && _parents.GetValueOrDefault(node) is not BlockStatement && _parents.GetValueOrDefault(node)?.Type != NodeType.Program)
        {
            // Source hoists only direct Program/Block declarations. Evaluating a
            // declaration elsewhere is a no-op; do not invent a native binding.
            IgnoredFunctions.Add(declaration);
            if (declaration.Id is not null) Bindings.Add(declaration.Id);
            return;
        }
        if (node is IFunction function)
        {
            var parameters = New(node, scope);
            Owned[node] = parameters;
            if (function.Id is not null) Bindings.Add(function.Id);
            foreach (var parameter in function.Params)
            {
                parameters.Native.UnionWith(Names(parameter));
                Mark(parameter);
                Visit(parameter, parameters);
            }
            Visit(function.Body, parameters);
            return;
        }
        if (node.Type == NodeType.Program || node is BlockStatement)
        {
            var block = rootOwned ? scope : New(node, scope);
            Owned[node] = block;
            At[node] = block;
            if (!rootOwned) Seed(block, node.ChildNodes, functions: true);
            foreach (var child in node.ChildNodes) Visit(child, block);
            return;
        }
        if (node is CatchClause handler)
        {
            var caught = New(node, scope);
            Owned[node] = caught;
            At[node] = caught;
            if (handler.Param is not null)
            {
                caught.Progressive.UnionWith(Names(handler.Param));
                Mark(handler.Param);
                Visit(handler.Param, caught);
            }
            Visit(handler.Body, caught);
            return;
        }
        if (node is ForOfStatement || node is ForInStatement)
        {
            var left = node is ForOfStatement each ? each.Left : ((ForInStatement)node).Left;
            var right = node is ForOfStatement values ? values.Right : ((ForInStatement)node).Right;
            var body = node is ForOfStatement loop ? loop.Body : ((ForInStatement)node).Body;
            if (left is VariableDeclaration binding)
            {
                var lexical = binding.Kind.ToString() != "Var";
                var outer = lexical ? New(node, scope) : scope;
                if (lexical) foreach (var item in binding.Declarations) outer.Native.UnionWith(Names(item.Id));
                Visit(right, outer);
                var iteration = New(node, outer);
                Iterations[node] = iteration;
                if (lexical) iteration.Native.UnionWith(outer.Native);
                Visit(left, iteration);
                Visit(body, iteration);
            }
            else
            {
                Visit(left, scope);
                Visit(right, scope);
                Visit(body, scope);
            }
            return;
        }
        if (node is ForStatement ordinary)
        {
            var loop = New(node, scope);
            Owned[node] = loop;
            ClassicLoops[node] = loop;
            if (ordinary.Init is VariableDeclaration init && init.Kind.ToString() != "Var")
                foreach (var item in init.Declarations) loop.Native.UnionWith(Names(item.Id));
            if (ordinary.Init is not null)
            {
                // Const header bindings cannot change. Its initializer closures
                // capture the initial scope, before body vars enter an iteration.
                var initial = ordinary.Init is VariableDeclaration constant && constant.Kind.ToString() == "Const" ? New(node, scope) : loop;
                if (!ReferenceEquals(initial, loop)) initial.Native.UnionWith(loop.Native);
                Visit(ordinary.Init, initial);
            }
            if (ordinary.Test is not null) Visit(ordinary.Test, loop);
            if (ordinary.Update is not null) Visit(ordinary.Update, loop);
            Visit(ordinary.Body, loop);
            return;
        }
        if (node is SwitchStatement choice)
        {
            Visit(choice.Discriminant, scope);
            var selected = New(node, scope);
            Owned[node] = selected;
            Seed(selected, choice.Cases.SelectMany(item => item.Consequent), functions: false);
            foreach (var item in choice.Cases) Visit(item, selected);
            return;
        }
        if (node is VariableDeclaration variables)
        {
            foreach (var variable in variables.Declarations)
            {
                if (variables.Kind.ToString() == "Var") scope.Progressive.UnionWith(Names(variable.Id));
                Mark(variable.Id);
            }
        }
        foreach (var child in node.ChildNodes) Visit(child, scope);
    }

    private void Mark(Node pattern)
    {
        foreach (var identifier in Identifiers(pattern)) Bindings.Add(identifier);
    }

    internal static IEnumerable<Identifier> Identifiers(Node pattern)
    {
        if (pattern is Identifier id) { yield return id; yield break; }
        var children = pattern switch
        {
            AssignmentPattern assignment => new[] { assignment.Left },
            RestElement rest => new[] { rest.Argument },
            Property property => new[] { property.Value },
            ArrayPattern or ObjectPattern => pattern.ChildNodes.ToArray(),
            _ => Array.Empty<Node>()
        };
        foreach (var child in children) foreach (var leaf in Identifiers(child)) yield return leaf;
    }

    private static IEnumerable<string> Names(Node pattern) => Identifiers(pattern).Select(id => id.Name);

    internal bool IsReference(Identifier identifier)
    {
        if (Bindings.Contains(identifier)) return false;
        var parent = _parents.GetValueOrDefault(identifier);
        if (parent is MemberExpression member && !member.Computed && ReferenceEquals(member.Property, identifier)) return false;
        if (parent is Property property && !property.Computed && ReferenceEquals(property.Key, identifier) && !property.Shorthand) return false;
        if (parent is LabeledStatement or BreakStatement or ContinueStatement) return false;
        return true;
    }
}
