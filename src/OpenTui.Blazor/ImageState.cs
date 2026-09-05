namespace OpenTui.Blazor;

using OpenTui.Native;

public enum ImageFit { Fit, Fill, Cover }
public sealed record TerminalImageContext(NativeTerminalCapabilities? Capabilities, int Columns, int Rows, int PixelWidth = 0, int PixelHeight = 0)
{
    public bool HasPixels => Columns > 0 && Rows > 0 && PixelWidth > 0 && PixelHeight > 0;
    public double CellAspectRatio => HasPixels ? (double)PixelHeight / Rows / ((double)PixelWidth / Columns) : 2;
    public NativeImageProtocol? Resolve(NativeImageProtocol requested)
    {
        if (Capabilities is not { } capabilities || requested == NativeImageProtocol.Blocks) return null;
        var selected = requested;
        if (selected == NativeImageProtocol.Auto)
        {
            selected = (NativeImageProtocol)capabilities.ImageProtocol;
            if (selected == NativeImageProtocol.Auto)
            {
                if (capabilities.Multiplexer == 1) return null;
                selected = capabilities.KittyGraphics ? NativeImageProtocol.Kitty : capabilities.Sixel && HasPixels ? NativeImageProtocol.Sixel : NativeImageProtocol.Blocks;
            }
        }
        return selected switch
        {
            NativeImageProtocol.Kitty when capabilities.KittyGraphics => selected,
            NativeImageProtocol.Sixel when capabilities.Sixel && HasPixels => selected,
            _ => null
        };
    }
}

/// <summary>Dispatcher-owned decoded image state. No source fetching or application permissions live here.</summary>
public sealed class ImageState : IDisposable
{
    private NativeImage? _image;
    private bool _disposed;
    private bool _renderEvent;
    public NativeImageInfo? Info => _image?.Info;
    public Exception? LoadError { get; private set; }
    public string? RenderError { get; private set; }
    public bool Loading { get; private set; }
    public NativeImageProtocol? EffectiveProtocol { get; private set; }
    public event Action? Changed;
    public void BeginLoad()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Loading = true; LoadError = null; RenderError = null;
        Changed?.Invoke();
    }
    public void SetEncoded(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Replace(NativeImage.Decode(bytes));
    }
    public void SetImage(NativeImage image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Replace(image.Retain());
    }
    private void Replace(NativeImage image)
    {
        var previous = _image;
        _image = image;
        Loading = false; LoadError = null; RenderError = null;
        previous?.Dispose();
        Changed?.Invoke();
    }
    public void Fail(Exception exception)
    {
        if (_disposed) return;
        Loading = false; LoadError = exception;
        Changed?.Invoke();
    }
    public void Clear()
    {
        var image = _image; _image = null;
        image?.Dispose();
        Loading = false; LoadError = null; RenderError = null;
        Changed?.Invoke();
    }

    internal void Draw(uint buffer, int x, int y, int width, int height, ImageFit fit, NativeImageProtocol requested, TerminalImageContext context)
    {
        if (_disposed || _image is null || width <= 0 || height <= 0) return;
        var protocol = context.Resolve(requested);
        if (protocol is null) { Report("No supported image graphics protocol is ready.", null); return; }
        try
        {
            var info = _image.Info;
            var drawWidth = width;
            var drawHeight = height;
            var sourceX = 0u;
            var sourceY = 0u;
            var sourceWidth = info.Width;
            var sourceHeight = info.Height;
            if (fit == ImageFit.Fit)
            {
                var aspect = (double)info.Width / info.Height * context.CellAspectRatio;
                var scale = Math.Min(width / aspect, height);
                drawWidth = Math.Max(1, checked((int)Math.Floor(aspect * scale + .5)));
                drawHeight = Math.Max(1, checked((int)Math.Floor(scale + .5)));
            }
            if (fit == ImageFit.Cover)
            {
                var aspect = width / (height * context.CellAspectRatio);
                if ((double)info.Width / info.Height > aspect)
                {
                    sourceWidth = Math.Min(info.Width, Math.Max(1u, checked((uint)Math.Floor(info.Height * aspect + .5))));
                    sourceX = (info.Width - sourceWidth) / 2;
                }
                else
                {
                    sourceHeight = Math.Min(info.Height, Math.Max(1u, checked((uint)Math.Floor(info.Width / aspect + .5))));
                    sourceY = (info.Height - sourceHeight) / 2;
                }
            }
            var accepted = _image.Draw(buffer, new NativeImageDrawOptions
            {
                X = x + (width - drawWidth) / 2, Y = y + (height - drawHeight) / 2,
                Width = (uint)drawWidth, Height = (uint)drawHeight,
                PixelWidth = context.HasPixels ? Math.Max(1u, checked((uint)Math.Floor((double)drawWidth * context.PixelWidth / context.Columns + .5))) : 0,
                PixelHeight = context.HasPixels ? Math.Max(1u, checked((uint)Math.Floor((double)drawHeight * context.PixelHeight / context.Rows + .5))) : 0,
                SourceX = sourceX, SourceY = sourceY, SourceWidth = sourceWidth, SourceHeight = sourceHeight, Protocol = protocol.Value
            });
            Report(accepted ? null : "Native image placement was rejected.", protocol);
        }
        catch (Exception exception) { Report(exception.Message, protocol); }
    }
    private void Report(string? error, NativeImageProtocol? protocol)
    {
        if (RenderError == error && EffectiveProtocol == protocol) return;
        RenderError = error; EffectiveProtocol = protocol; _renderEvent = true;
    }
    internal bool FlushRenderEvent()
    {
        if (!_renderEvent || _disposed) return false;
        _renderEvent = false; Changed?.Invoke(); return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _image?.Dispose(); _image = null;
    }
}
