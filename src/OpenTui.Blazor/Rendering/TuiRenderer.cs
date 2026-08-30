namespace OpenTui.Blazor.Rendering;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging;
using OpenTui.Blazor.Nodes;
using OpenTui.Native;

public sealed class TuiRenderer : Renderer
{
    private readonly TuiNode _rootNode = new() { TagName = "root" };
    private readonly Dictionary<int, TuiNode> _componentRoots = [];
    private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();

    public TuiNode RootNode => _rootNode;

    public TuiRenderer(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        : base(serviceProvider, loggerFactory)
    {
    }

    public override Dispatcher Dispatcher => _dispatcher;

    public Task<int> AttachRootComponentAsync<TComponent>() where TComponent : IComponent
    {
        return Dispatcher.InvokeAsync(async () =>
        {
            var component = (IComponent)InstantiateComponent(typeof(TComponent));
            var componentId = AssignRootComponentId(component);
            var componentRootNode = new TuiNode { TagName = typeof(TComponent).Name };
            _componentRoots[componentId] = componentRootNode;
            _rootNode.AddChild(componentRootNode);

            await RenderRootComponentAsync(componentId, ParameterView.Empty);
            return componentId;
        });
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        // 1. Process updated components
        for (int i = 0; i < renderBatch.UpdatedComponents.Count; i++)
        {
            var diff = renderBatch.UpdatedComponents.Array[i];
            if (!_componentRoots.TryGetValue(diff.ComponentId, out var componentRoot))
            {
                continue;
            }

            ApplyDiff(componentRoot, diff, renderBatch.ReferenceFrames.Array);
        }

        // 2. Process disposed components
        for (int i = 0; i < renderBatch.DisposedComponentIDs.Count; i++)
        {
            int disposedId = renderBatch.DisposedComponentIDs.Array[i];
            if (_componentRoots.Remove(disposedId, out var node))
            {
                node.Parent?.RemoveChild(node);
            }
        }

        return Task.CompletedTask;
    }

    private static void ApplyDiff(TuiNode componentRoot, in RenderTreeDiff diff, RenderTreeFrame[] frames)
    {
        var stack = new Stack<TuiNode>();
        stack.Push(componentRoot);

        for (int i = 0; i < diff.Edits.Count; i++)
        {
            var edit = diff.Edits.Array[i];
            var current = stack.Peek();

            switch (edit.Type)
            {
                case RenderTreeEditType.PrependFrame:
                {
                    var frame = frames[edit.ReferenceFrameIndex];
                    if (frame.FrameType == RenderTreeFrameType.Element)
                    {
                        var elementNode = new TuiNode { TagName = frame.ElementName };
                        current.AddChild(elementNode);
                    }
                    else if (frame.FrameType == RenderTreeFrameType.Text)
                    {
                        current.TextContent = frame.TextContent;
                    }
                    break;
                }

                case RenderTreeEditType.SetAttribute:
                {
                    var frame = frames[edit.ReferenceFrameIndex];
                    if (frame.FrameType == RenderTreeFrameType.Attribute)
                    {
                        ApplyAttribute(current, frame.AttributeName, frame.AttributeValue);
                    }
                    break;
                }

                case RenderTreeEditType.UpdateText:
                {
                    var frame = frames[edit.ReferenceFrameIndex];
                    current.TextContent = frame.TextContent;
                    break;
                }

                case RenderTreeEditType.StepIn:
                {
                    if (current.Children.Count > edit.SiblingIndex)
                    {
                        stack.Push(current.Children[edit.SiblingIndex]);
                    }
                    break;
                }

                case RenderTreeEditType.StepOut:
                {
                    if (stack.Count > 1)
                    {
                        stack.Pop();
                    }
                    break;
                }

                case RenderTreeEditType.RemoveFrame:
                {
                    if (current.Children.Count > edit.SiblingIndex)
                    {
                        current.RemoveChild(current.Children[edit.SiblingIndex]);
                    }
                    break;
                }
            }
        }
    }

    private static void ApplyAttribute(TuiNode node, string name, object? value)
    {
        switch (name.ToLowerInvariant())
        {
            case "direction":
                node.Direction = value?.ToString()?.Equals("row", StringComparison.OrdinalIgnoreCase) == true
                    ? TuiFlexDirection.Row
                    : TuiFlexDirection.Column;
                break;
            case "border":
                node.BorderStyle = value?.ToString();
                break;
            case "width":
                if (value is int w) node.Width = w;
                break;
            case "height":
                if (value is int h) node.Height = h;
                break;
            case "gap":
                if (value is int g) node.Gap = g;
                break;
            case "bold":
                node.Bold = value is bool b && b;
                break;
            case "dim":
                node.Dim = value is bool d && d;
                break;
            case "fg":
                if (value is string hex && hex.StartsWith('#'))
                {
                    var hexTrim = hex.TrimStart('#');
                    if (hexTrim.Length == 6)
                    {
                        byte red = Convert.ToByte(hexTrim[..2], 16);
                        byte green = Convert.ToByte(hexTrim[2..4], 16);
                        byte blue = Convert.ToByte(hexTrim[4..6], 16);
                        node.Fg = new NativeRgba(red, green, blue);
                    }
                }
                break;
            case "value":
                node.TextContent = value?.ToString() ?? "";
                break;
        }
    }

    protected override void HandleException(Exception exception)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Blazor OpenTUI Exception]: {exception.Message}");
        Console.ResetColor();
    }
}
