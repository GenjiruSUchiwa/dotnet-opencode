namespace OpenTui.Blazor;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.ExceptionServices;
using OpenTui.Blazor.Rendering;
using OpenTui.Native;
using OpenTui.Blazor.Clipboard;

/// <summary>
/// Owns one terminal application lifetime. Await RunAsync before disposing the host;
/// cancel that call to stop it. Rendering, input and resizing share one dispatcher.
/// </summary>
public sealed class OpenTuiHost : IAsyncDisposable
{
    private readonly ServiceProvider? _ownedServices;
    private int _started;
    private int _disposed;
    public TuiRenderer Renderer { get; }
    public bool TerminalFocused { get; private set; } = true;
    public NativeTerminalCapabilities? Capabilities { get; private set; }
    public TerminalInteractionOptions Interaction { get; }
    public ITextClipboard Clipboard { get; }
    public TimeProvider Clock { get; }
    private readonly IClipboardReader? _clipboardReader;
    private NativeHostClipboard? _ownedClipboardReader;
    public TerminalRenderColors Colors { get => Renderer.Colors; set => Renderer.Colors = value; }

    public OpenTuiHost(IServiceProvider? services = null, TerminalInteractionOptions? interaction = null, ITextClipboard? clipboard = null,
        IClipboardReader? clipboardReader = null, TimeProvider? clock = null)
    {
        Clock = clock ?? services?.GetService<TimeProvider>() ?? TimeProvider.System;
        if (services is null) services = _ownedServices = new ServiceCollection().AddSingleton(Clock).BuildServiceProvider();
        Renderer = new TuiRenderer(new ClockServices(services, Clock), NullLoggerFactory.Instance, Clock);
        Interaction = interaction ?? new();
        Clipboard = clipboard ?? new WindowsTextClipboard();
        _clipboardReader = clipboardReader ?? clipboard as IClipboardReader;
    }

    /// <summary>Explicit user-action boundary. No reader/native service is created by host startup.</summary>
    public Task<ClipboardReadResult> ReadClipboardAsync(ClipboardReadRequest request, CancellationToken cancellationToken = default)
    {
        Renderer.Dispatcher.AssertAccess();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return (_clipboardReader ?? (_ownedClipboardReader ??= new NativeHostClipboard(clock: Clock))).ReadAsync(request, cancellationToken);
    }

    public async Task RunAsync<TComponent>(ParameterView? parameters = null, string title = "OpenTUI", CancellationToken cancellationToken = default)
        where TComponent : class, IComponent, ITerminalApp
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A terminal host can run only once. Create a new host for another terminal lifetime.");
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        NativeTerminal? terminal = null;
        WindowsConsoleInput? consoleInput = null;
        UnixTerminalInput? unixInput = null;
        TerminalInput? input = null;
        NativeImageCache? imageCache = null;
        TerminalPixelQuery? pixelQuery = null;
        TComponent? app = null;
        var failures = new List<Exception>();
        try
        {
            var size = OperatingSystem.IsWindows()
                ? new NativeTerminalSize(Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight), 0, 0)
                : NativeTerminal.GetUnixSize();
            var width = size.Width;
            var height = size.Height;
            terminal = new NativeTerminal(width, height);
            imageCache = new NativeImageCache();
            if (terminal.InputKind == NativeTerminalInputKind.WindowsConsoleEvents) consoleInput = new WindowsConsoleInput();
            var mouseEnabled = Interaction.MouseEnabled;
            if (mouseEnabled) OpenTuiNative.EnableMouse(terminal.Renderer, true);
            OpenTuiNative.SetTitle(terminal.Renderer, title);
            app = await Renderer.AttachRootComponentAsync<TComponent>(parameters);
            Capabilities = OpenTuiNative.GetTerminalCapabilities(terminal.Renderer);
            Renderer.ImageContext = new(Capabilities, width, height, size.PixelWidth, size.PixelHeight);
            await Renderer.Dispatcher.InvokeAsync(() => app.OnCapabilitiesChanged(Capabilities));
            var layout = new TuiLayoutEngine();
            var force = true;
            var pending = new Queue<object>();
            var nativePixels = new TerminalPixelSize(size.PixelWidth, size.PixelHeight);
            pixelQuery = new(() => OpenTuiNative.QueryPixelResolution(terminal.Renderer));
            input = new TerminalInput(key => pending.Enqueue(key), text => pending.Enqueue(text), text =>
            {
                OpenTuiNative.ProcessResponse(terminal.Renderer, text);
                var capabilities = OpenTuiNative.GetTerminalCapabilities(terminal.Renderer);
                if (Capabilities != capabilities)
                {
                    Capabilities = capabilities;
                    Renderer.ImageContext = Renderer.ImageContext with { Capabilities = capabilities };
                    layout.InvalidateTextMetrics(Renderer.RootNode);
                    layout.UpdateLayout(Renderer.RootNode, width, height, OpenTuiNative.GetNextBuffer(terminal.Renderer));
                    app.OnCapabilitiesChanged(capabilities);
                }
                force = Renderer.Dirty = true;
            }, focus: focused =>
            {
                if (TerminalFocused == focused) return;
                TerminalFocused = focused;
                if (!focused) Renderer.ResetPointerState(layout);
                if (focused) OpenTuiNative.RestoreTerminalModes(terminal.Renderer);
                app.OnTerminalFocusChanged(focused);
                force = Renderer.Dirty = true;
            }, consumeResponse: text =>
            {
                if (!pixelQuery.Consume(text, out var reply)) return false;
                if (reply is { } pixels)
                {
                    var actual = nativePixels.Width > 0 && nativePixels.Height > 0 ? nativePixels : pixels;
                    Renderer.ImageContext = new(Capabilities, width, height, actual.Width, actual.Height);
                    force = true;
                }
                return true;
            }, pasteRejected: app.OnInputError, pointer: mouse => pending.Enqueue(mouse),
                inputRejected: app.OnInputError, richKey: key => pending.Enqueue(key), clock: Clock);
            if (terminal.InputKind == NativeTerminalInputKind.UnixBytes) unixInput = new UnixTerminalInput(terminal, input);
            // Both platform readers now feed a parser with consumed-response
            // routing installed before the native query is emitted.
            pixelQuery.Request();
            await Renderer.Dispatcher.InvokeAsync(() => app.Resize(width, height));
            while (!cancellationToken.IsCancellationRequested)
            {
                var exit = await Renderer.Dispatcher.InvokeAsync(() =>
                {
                    app.TickKeymap(TimeSpan.FromMilliseconds(Clock.GetTimestampMilliseconds()));
                    layout.Colors = Renderer.Colors;
                    Renderer.WheelScrollSpeed = Interaction.ScrollSpeed;
                    if (mouseEnabled != Interaction.MouseEnabled)
                    {
                        mouseEnabled = Interaction.MouseEnabled;
                        Renderer.ResetPointerState(layout);
                        if (mouseEnabled) OpenTuiNative.EnableMouse(terminal.Renderer, true);
                        else OpenTuiNative.DisableMouse(terminal.Renderer);
                    }
                    Renderer.ObservePendingEvents();
                    Renderer.ScrollSelection(TimeSpan.FromMilliseconds(Clock.GetTimestampMilliseconds()));
                    if (Renderer.Error is Exception error) throw new InvalidOperationException("Blazor rendering failed.", error);
                    var currentSize = terminal.InputKind == NativeTerminalInputKind.WindowsConsoleEvents
                        ? new NativeTerminalSize(Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight), 0, 0)
                        : NativeTerminal.GetUnixSize();
                    var newWidth = currentSize.Width;
                    var newHeight = currentSize.Height;
                    if (width != newWidth || height != newHeight)
                    {
                        width = newWidth;
                        height = newHeight;
                        terminal.Resize(width, height);
                        nativePixels = new(currentSize.PixelWidth, currentSize.PixelHeight);
                        Renderer.ImageContext = new(Capabilities, width, height, currentSize.PixelWidth, currentSize.PixelHeight);
                        pixelQuery.Request();
                        Renderer.ResetPointerState(layout);
                        app.Resize(width, height);
                        force = true;
                    }
                    else if (currentSize.PixelWidth > 0 && currentSize.PixelHeight > 0)
                    {
                        nativePixels = new(currentSize.PixelWidth, currentSize.PixelHeight);
                        Renderer.ImageContext = Renderer.ImageContext with { PixelWidth = currentSize.PixelWidth, PixelHeight = currentSize.PixelHeight };
                    }
                    // Bound input work so large pastes cannot starve streaming and painting.
                    if (force)
                    {
                        app.OnFrame();
                        layout.UpdateLayout(Renderer.RootNode, width, height, OpenTuiNative.GetNextBuffer(terminal.Renderer));
                    }
                    var endOfInput = false;
                    if (consoleInput is not null)
                    {
                        for (var count = 0; count < 256 && consoleInput.TryRead(out var next); count++)
                        {
                            if (next is ConsoleKeyInfo key) input.Feed(key);
                            else if (next is TerminalPointerInput mouse) pending.Enqueue(mouse);
                        }
                        input.FlushEscape();
                    }
                    else if (unixInput is not null) endOfInput = unixInput.ReadFrame().Status == UnixInputStatus.EndOfStream;
                    while (pending.TryDequeue(out var item))
                    {
                        if (Renderer.Dirty)
                        {
                            layout.UpdateLayout(Renderer.RootNode, width, height, OpenTuiNative.GetNextBuffer(terminal.Renderer));
                            Renderer.EnsureFocus();
                            Renderer.DispatchLayoutEvents();
                            Renderer.RefreshSelection(layout);
                        }
                        if (item is TerminalKeyInput key) DispatchInput(app, key, layout);
                        else if (item is ConsoleKeyInfo legacy) DispatchInput(app, TerminalKeyInput.FromConsole(legacy), layout);
                        else if (item is string text && !Renderer.DispatchPaste(text)) app.Paste(text);
                        else if (item is TerminalPointerInput mouse && TerminalFocused && mouseEnabled) Renderer.DispatchPointer(mouse, layout);
                    }
                    if (app.ExitRequested || endOfInput) return true;
                    app.OnFrame();
                    if (Renderer.Error is Exception frameError) throw new InvalidOperationException("Blazor rendering failed.", frameError);
                    if (!Renderer.Dirty && !force) return false;
                    var buffer = OpenTuiNative.GetNextBuffer(terminal.Renderer);
                    if (buffer == 0) throw new InvalidOperationException("OpenTUI returned an invalid frame buffer.");
                    layout.Colors = Renderer.Colors;
                    layout.ImageContext = Renderer.ImageContext;
                    OpenTuiNative.Clear(buffer, Renderer.Colors.Background);
                    layout.UpdateLayout(Renderer.RootNode, width, height, buffer);
                    Renderer.EnsureFocus();
                    var layoutEvents = Renderer.DispatchLayoutEvents();
                    Renderer.RefreshSelection(layout);
                    layout.ImageContext = Renderer.ImageContext;
                    layout.Render(Renderer.RootNode, terminal.Renderer, buffer);
                    var status = OpenTuiNative.Render(terminal.Renderer, force);
                    var imageEvents = Renderer.DispatchImageEvents();
                    if (status == 2) throw new IOException("OpenTUI could not write the terminal frame.");
                    Renderer.Dirty = status == 1 || layoutEvents || imageEvents;
                    force = status == 1 || layoutEvents || imageEvents;
                    return false;
                });
                if (exit) break;
                await Task.Delay(TimeSpan.FromMilliseconds(16), Clock, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { failures.Add(exception); }
        finally
        {
            pixelQuery?.Stop();
            // Each cleanup step runs even if an earlier one failed. Native teardown
            // occurs only after input/render work and component cancellation finish.
            if (app is not null)
            {
                async Task StopAppAsync() => await app.StopAsync();
                try { await Renderer.Dispatcher.InvokeAsync(() => Task.WhenAll(Renderer.StopPendingEventsAsync(), StopAppAsync())); }
                catch (Exception exception) { failures.Add(exception); }
                try { await Renderer.DetachRootComponentAsync(app); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (Renderer.Error is { } renderError && !failures.Any(exception => ReferenceEquals(exception, renderError)
                || ReferenceEquals(exception.InnerException, renderError)
                || exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Contains(renderError)))
                failures.Add(renderError);
            try { if (terminal is not null) OpenTuiNative.DisableMouse(terminal.Renderer); }
            catch (Exception exception) { failures.Add(exception); }
            try { unixInput?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { input?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { consoleInput?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { terminal?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { imageCache?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { await DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        ThrowFailures(failures);
    }

    private void DispatchInput(ITerminalApp app, TerminalKeyInput key, TuiLayoutEngine layout)
    {
        var press = key.EventType == Keymap.KeyEventType.Press;
        var ordinaryModifiers = !key.Meta && !key.Super && !key.Hyper;
        if (press && ordinaryModifiers && Renderer.Selection is { HasText: true } &&
            (key.Name == "escape" && !key.Ctrl || key.Name == "c" && key.Ctrl))
        {
            if (key.Name == "escape") Renderer.ClearSelection();
            else Renderer.CopySelection(Clipboard, app.OnInputError);
            return;
        }
        if (Renderer.DispatchEmbeddedKey(key, app.OnInputError)) return;
        var routed = app.DispatchKeymap(key, Renderer.KeymapContext(layout), TimeSpan.FromMilliseconds(Clock.GetTimestampMilliseconds()));
        if (routed?.StopPropagation == true) return;
        if (Renderer.DispatchTextareaKey(key, routed?.PreventDefault == true)) return;
        if (!press || routed?.PreventDefault == true) return;
        if (Renderer.DispatchTextInput(key)) return;
        var projection = key.ProjectConsoleKeys();
        const TerminalKeyProjectionLoss compatible = TerminalKeyProjectionLoss.Utf16Split | TerminalKeyProjectionLoss.LinefeedAlias;
        if (projection.SuppressedRelease || (projection.Loss & ~compatible) != TerminalKeyProjectionLoss.None)
        {
            app.OnInputError("This editor has no rich-key consumer for the reported input; it was not flattened into a legacy press.");
            return;
        }
        foreach (var legacy in projection.Keys)
        {
            // The two pre-existing compatibility bridges still need the legacy
            // app's focus/context setup and Ctrl+J binding. Exact presses were
            // already dispatched above and must not execute commands twice.
            var legacyRoute = projection.IsExact ? routed : app.DispatchKeymap(legacy, Renderer.KeymapContext(layout), TimeSpan.FromMilliseconds(Clock.GetTimestampMilliseconds()));
            if (legacyRoute?.PreventDefault != true && legacyRoute?.StopPropagation != true && !Renderer.DispatchKey(legacy, layout)) app.HandleKey(legacy);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var failures = new List<Exception>();
        try { await Renderer.Dispatcher.InvokeAsync(Renderer.StopPendingEventsAsync); }
        catch (Exception exception) { failures.Add(exception); }
        try { if (_ownedClipboardReader is not null) await _ownedClipboardReader.DisposeAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        try { await Renderer.DisposeAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        try { if (_ownedServices is not null) await _ownedServices.DisposeAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        ThrowFailures(failures);
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Terminal execution or cleanup failed.", failures);
    }
}
