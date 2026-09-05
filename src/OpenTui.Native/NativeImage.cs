namespace OpenTui.Native;

using System.Runtime.InteropServices;

public enum NativeImageStatus : uint
{
    Ok, InvalidHandle, UnsupportedFormat, UnsupportedColorSpace, MalformedInput, DimensionLimit,
    MemoryLimit, InvalidArgument, OutOfMemory, OutputTooSmall, InternalError, UnsupportedFeature
}
public enum NativeImageFormat : uint { Unknown, Png, RawRgba, Jpeg, Webp, Gif }
public enum NativeImageColorStatus : uint { AssumedSrgb, ExplicitSrgb }
public enum NativeImageResizeFilter : uint { Default, Area, Triangle, CubicBSpline, CatmullRom, Mitchell, Nearest }
public enum NativeImageProtocol : uint { Auto, Kitty, Sixel, Blocks }

/// <summary>Verified native Info ABI: eight consecutive u32 values, 32 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeImageInfo
{
    public readonly uint Width, Height, SourceWidth, SourceHeight;
    public readonly NativeImageFormat Format;
    public readonly NativeImageColorStatus ColorStatus;
    public readonly uint Orientation;
    private readonly uint _hasAlpha;
    public bool HasAlpha => _hasAlpha != 0;
}

/// <summary>Verified draw ABI: i32 x/y followed by nine u32 values, 44 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeImageDrawOptions
{
    public int X, Y;
    public uint Width, Height, PixelWidth, PixelHeight, SourceX, SourceY, SourceWidth, SourceHeight;
    public NativeImageProtocol Protocol;
}

public sealed class NativeImageException(NativeImageStatus status) : Exception($"Native image operation failed: {status} ({(uint)status}).")
{
    public NativeImageStatus Status { get; } = status;
}

/// <summary>Hold while a renderer can retain lazy image placements, and around standalone image lifetimes.</summary>
public sealed class NativeImageCache : IDisposable
{
    private bool _held;
    public NativeImageCache() { OpenTuiNative.RetainImageIccCache(); _held = true; }
    public void Dispose() { if (!_held) return; _held = false; OpenTuiNative.ReleaseImageIccCache(); }
}

/// <summary>Owns one image handle. Encoded input is copied by native decode; no pixel pointer escapes.</summary>
public sealed class NativeImage : IDisposable
{
    public const int MaximumEncodedBytes = 64 * 1024 * 1024;
    public const uint MaximumDimension = 16384;
    public const ulong MaximumPixels = 25_000_000;
    public const ulong MaximumDecodedBytes = 100 * 1024 * 1024;
    private uint _handle;
    private readonly NativeImageCache _cache;
    public NativeImageInfo Info { get; }

    private NativeImage(uint handle, NativeImageInfo info, NativeImageCache cache) { _handle = handle; Info = info; _cache = cache; }
    public static NativeImageInfo Inspect(ReadOnlySpan<byte> encoded)
    {
        CheckEncoded(encoded);
        using var cache = new NativeImageCache();
        Check(OpenTuiNative.InspectImage(encoded, out var info));
        ValidateInfo(info);
        return info;
    }
    public static NativeImage Decode(ReadOnlySpan<byte> encoded)
    {
        CheckEncoded(encoded);
        var cache = new NativeImageCache();
        try { Check(OpenTuiNative.DecodeImage(encoded, out var handle)); return Adopt(handle, cache); }
        catch { cache.Dispose(); throw; }
    }
    public static NativeImage FromRgba(ReadOnlySpan<byte> pixels, uint width, uint height, uint stride)
    {
        ValidateDimensions(width, height);
        if (stride < checked(width * 4) || checked((ulong)stride * (height - 1) + width * 4) > (ulong)pixels.Length)
            throw new NativeImageException(NativeImageStatus.InvalidArgument);
        var cache = new NativeImageCache();
        try { Check(OpenTuiNative.CreateImageRgba(pixels, width, height, stride, out var handle)); return Adopt(handle, cache); }
        catch { cache.Dispose(); throw; }
    }
    public NativeImage Retain()
    {
        Guard(); var cache = new NativeImageCache();
        try { Check(OpenTuiNative.RetainImage(_handle, out var handle)); return Adopt(handle, cache); }
        catch { cache.Dispose(); throw; }
    }
    public NativeImage Clone()
    {
        Guard(); var cache = new NativeImageCache();
        try { Check(OpenTuiNative.CloneImage(_handle, out var handle)); return Adopt(handle, cache); }
        catch { cache.Dispose(); throw; }
    }
    public NativeImage Resize(uint width, uint height, NativeImageResizeFilter filter = NativeImageResizeFilter.Default)
    {
        Guard(); ValidateDimensions(width, height);
        if (!Enum.IsDefined(filter)) throw new ArgumentOutOfRangeException(nameof(filter));
        var cache = new NativeImageCache();
        try { Check(OpenTuiNative.ResizeImage(_handle, width, height, filter, out var handle)); return Adopt(handle, cache); }
        catch { cache.Dispose(); throw; }
    }
    public NativeImage Extract(uint left, uint top, uint width, uint height)
    {
        Guard(); ValidateDimensions(width, height);
        if ((ulong)left + width > Info.Width || (ulong)top + height > Info.Height) throw new NativeImageException(NativeImageStatus.InvalidArgument);
        var cache = new NativeImageCache();
        try { Check(OpenTuiNative.ExtractImage(_handle, left, top, width, height, out var handle)); return Adopt(handle, cache); }
        catch { cache.Dispose(); throw; }
    }
    public byte[] CopyPixels(bool bgra = false)
    {
        Guard();
        var stride = checked(Info.Width * 4);
        var pixels = new byte[checked((int)((ulong)stride * Info.Height))];
        Check(OpenTuiNative.CopyImagePixels(_handle, pixels, stride, bgra));
        return pixels;
    }
    public void Materialize() { Guard(); Check(OpenTuiNative.MaterializeImage(_handle)); }
    /// <summary>Ensures native-owned PNG data for rendering; this ABI does not expose encoded PNG bytes to callers.</summary>
    public void EnsureEncodedPng() { Guard(); Check(OpenTuiNative.EnsureImagePng(_handle)); }
    public bool Draw(uint buffer, NativeImageDrawOptions options)
    {
        Guard(); ArgumentOutOfRangeException.ThrowIfZero(buffer);
        if (!Enum.IsDefined(options.Protocol) || options.Width == 0 || options.Height == 0 || options.SourceWidth == 0 || options.SourceHeight == 0 ||
            (ulong)options.SourceX + options.SourceWidth > Info.Width || (ulong)options.SourceY + options.SourceHeight > Info.Height)
            throw new NativeImageException(NativeImageStatus.InvalidArgument);
        return OpenTuiNative.DrawImage(buffer, _handle, in options);
    }
    private static NativeImage Adopt(uint handle, NativeImageCache cache)
    {
        if (handle == 0) throw new NativeImageException(NativeImageStatus.InternalError);
        try
        {
            Check(OpenTuiNative.GetImageInfo(handle, out var info));
            ValidateInfo(info);
            return new(handle, info, cache);
        }
        catch { OpenTuiNative.DestroyImage(handle); throw; }
    }
    private static void CheckEncoded(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) throw new NativeImageException(NativeImageStatus.InvalidArgument);
        if (bytes.Length > MaximumEncodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
    }
    private static void ValidateInfo(NativeImageInfo info)
    {
        ValidateDimensions(info.Width, info.Height);
        if (info.Format == NativeImageFormat.Unknown || !Enum.IsDefined(info.Format)) throw new NativeImageException(NativeImageStatus.UnsupportedFormat);
    }
    private static void ValidateDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension || (ulong)width * height > MaximumPixels)
            throw new NativeImageException(NativeImageStatus.DimensionLimit);
        if ((ulong)width * height * 4 > MaximumDecodedBytes) throw new NativeImageException(NativeImageStatus.MemoryLimit);
    }
    private static void Check(uint status) { if (status != 0) throw new NativeImageException((NativeImageStatus)status); }
    private void Guard() => ObjectDisposedException.ThrowIf(_handle == 0, this);
    public void Dispose()
    {
        var handle = _handle; _handle = 0;
        if (handle == 0) return;
        try { OpenTuiNative.DestroyImage(handle); }
        finally { _cache.Dispose(); }
    }
}
