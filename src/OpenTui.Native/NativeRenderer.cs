namespace OpenTui.Native;

/// <summary>Owns an OpenTUI renderer. Dispose on the rendering thread before restoring OS terminal modes.</summary>
/// <remarks>
/// Calls and disposal must be serialized by the host. Handles returned by this owner are borrowed:
/// never destroy them directly or use them after disposal. Reacquire buffers after resizing.
/// This owner does not configure OS input modes or parse terminal input.
/// </remarks>
public sealed class NativeRenderer : IDisposable
{
    private uint _handle;

    public uint Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return _handle;
        }
    }

    /// <param name="width">Positive terminal width in cells.</param>
    /// <param name="height">Positive terminal height in cells.</param>
    /// <param name="bufferedOutputKind">0 for stdout; 1 for memory.</param>
    /// <param name="remoteMode">0 for automatic detection; 1 for local; 2 for remote.</param>
    /// <param name="feedPtr">Optional borrowed native span feed; must outlive this renderer.</param>
    public NativeRenderer(int width, int height, byte bufferedOutputKind = 0, byte remoteMode = 0, IntPtr feedPtr = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bufferedOutputKind, (byte)1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remoteMode, (byte)2);
        _handle = OpenTuiNative.CreateRenderer((uint)width, (uint)height, bufferedOutputKind, remoteMode, feedPtr);
        if (_handle == 0) throw new InvalidOperationException("OpenTUI could not create a native renderer.");
    }

    public uint GetNextBuffer()
    {
        var buffer = OpenTuiNative.GetNextBuffer(Handle);
        return buffer != 0 ? buffer : throw new InvalidOperationException("OpenTUI could not acquire the next buffer.");
    }

    public uint GetCurrentBuffer()
    {
        var buffer = OpenTuiNative.GetCurrentBuffer(Handle);
        return buffer != 0 ? buffer : throw new InvalidOperationException("OpenTUI could not acquire the current buffer.");
    }

    public void Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        OpenTuiNative.ResizeRenderer(Handle, (uint)width, (uint)height);
    }

    /// <returns>0 on success, 1 when skipped, or 2 on failure. Skipped frames may need retrying.</returns>
    public byte Render(bool force = false) => OpenTuiNative.Render(Handle, force);

    public void Dispose()
    {
        if (_handle == 0) return;
        var handle = _handle;
        _handle = 0;
        OpenTuiNative.DestroyRenderer(handle, false);
    }
}
