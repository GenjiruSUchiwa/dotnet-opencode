namespace OpenTui.Blazor;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTui.Blazor.Components;
using OpenTui.Blazor.Rendering;

public sealed class OpenTuiHost
{
    private readonly TuiRenderer _renderer;

    public TuiRenderer Renderer => _renderer;

    public OpenTuiHost(IServiceProvider? services = null)
    {
        services ??= new ServiceCollection().BuildServiceProvider();
        var loggerFactory = NullLoggerFactory.Instance;
        _renderer = new TuiRenderer(services, loggerFactory);
    }

    public async Task<int> RunAppAsync()
    {
        return await _renderer.AttachRootComponentAsync<OpenCodeApp>();
    }
}
