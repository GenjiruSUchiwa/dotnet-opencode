namespace OpenTui.Native;

public sealed record NativeSyntaxRule(string Scope, NativeRgba? Foreground = null, NativeRgba? Background = null,
    uint Attributes = 0, uint AttributeMask = uint.MaxValue);

/// <summary>Owns a native style registry. Attached views lease the handle until they detach or are destroyed.</summary>
public sealed class NativeSyntaxStyle : IDisposable
{
    private uint _handle;
    private int _references = 1;
    private bool _disposed;
    public NativeSyntaxStyle()
    {
        _handle = OpenTuiNative.CreateSyntaxStyle();
        if (_handle == 0) throw new InvalidOperationException("Native syntax style creation failed.");
    }
    public uint Register(NativeSyntaxRule rule)
    {
        Guard(); ArgumentException.ThrowIfNullOrEmpty(rule.Scope);
        var id = OpenTuiNative.RegisterSyntax(_handle, rule);
        return id != 0 ? id : throw new InvalidOperationException($"Could not register native syntax scope '{rule.Scope}'.");
    }
    public uint? Resolve(string scope)
    {
        Guard();
        var id = OpenTuiNative.ResolveSyntax(_handle, scope);
        return id == 0 ? null : id;
    }
    public uint Count { get { Guard(); return OpenTuiNative.SyntaxStyleCount(_handle); } }
    internal Lease Retain() { Guard(); _references++; return new(this, _handle); }
    private void Guard() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void Release()
    {
        if (--_references != 0) return;
        var handle = _handle; _handle = 0;
        OpenTuiNative.DestroySyntaxStyle(handle);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Release(); }
    internal sealed class Lease(NativeSyntaxStyle owner, uint handle) : IDisposable
    {
        private NativeSyntaxStyle? _owner = owner;
        internal uint Handle { get; } = handle;
        public void Dispose() { var value = _owner; _owner = null; value?.Release(); }
    }
}
