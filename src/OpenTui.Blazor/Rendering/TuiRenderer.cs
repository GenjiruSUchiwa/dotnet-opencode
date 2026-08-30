namespace OpenTui.Blazor.Rendering;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging;
using OpenTui.Blazor.Nodes;
using OpenTui.Native;

public sealed class TuiRenderer : Renderer
{
    private readonly TuiNode _rootNode = new() { TagName = "root" };
    private readonly Dictionary<int, TuiNode> _nodesById = [];
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
            await RenderRootComponentAsync(componentId, ParameterView.Empty);
            return componentId;
        });
    }

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        // Apply element diffs to TuiNode visual tree
        for (int i = 0; i < renderBatch.UpdatedComponents.Count; i++)
        {
            var componentId = renderBatch.UpdatedComponents.Array[i].ComponentId;
            var edits = renderBatch.UpdatedComponents.Array[i].Edits;

            // Update nodes based on render tree edits
            for (int e = 0; e < edits.Count; e++)
            {
                var edit = edits.Array[e];
                switch (edit.Type)
                {
                    case RenderTreeEditType.PrependFrame:
                        var frame = renderBatch.ReferenceFrames.Array[edit.ReferenceFrameIndex];
                        if (frame.FrameType == RenderTreeFrameType.Text)
                        {
                            _rootNode.TextContent = frame.TextContent;
                        }
                        break;
                }
            }
        }

        return Task.CompletedTask;
    }

    protected override void HandleException(Exception exception)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Blazor OpenTUI Exception]: {exception.Message}");
        Console.ResetColor();
    }
}
