namespace OpenTui.Native;

using System.Runtime.InteropServices;

public static unsafe partial class OpenTuiNative
{
    [LibraryImport(LibName, EntryPoint = "imageRetainIccCache")]
    internal static partial void RetainImageIccCache();
    [LibraryImport(LibName, EntryPoint = "imageReleaseIccCache")]
    internal static partial void ReleaseImageIccCache();
    [LibraryImport(LibName, EntryPoint = "imageInfo")]
    private static partial uint InspectImageCore(byte* bytes, uint length, out NativeImageInfo info);
    [LibraryImport(LibName, EntryPoint = "imageDecode")]
    private static partial uint DecodeImageCore(byte* bytes, uint length, out uint image);
    [LibraryImport(LibName, EntryPoint = "imageDestroy")]
    internal static partial void DestroyImage(uint image);
    [LibraryImport(LibName, EntryPoint = "imageRetain")]
    internal static partial uint RetainImage(uint image, out uint retained);
    [LibraryImport(LibName, EntryPoint = "imageGetInfo")]
    internal static partial uint GetImageInfo(uint image, out NativeImageInfo info);
    [LibraryImport(LibName, EntryPoint = "imageMaterialize")]
    internal static partial uint MaterializeImage(uint image);
    [LibraryImport(LibName, EntryPoint = "imageEnsureEncodedPng")]
    internal static partial uint EnsureImagePng(uint image);
    [LibraryImport(LibName, EntryPoint = "imageCopyPixels")]
    private static partial uint CopyImagePixelsCore(uint image, byte* destination, ulong length, uint stride, byte bgra);
    [LibraryImport(LibName, EntryPoint = "imageCreateFromRgba")]
    private static partial uint CreateImageRgbaCore(byte* pixels, ulong length, uint width, uint height, uint stride, out uint image);
    [LibraryImport(LibName, EntryPoint = "imageResize")]
    internal static partial uint ResizeImage(uint image, uint width, uint height, NativeImageResizeFilter filter, out uint resized);
    [LibraryImport(LibName, EntryPoint = "imageExtract")]
    internal static partial uint ExtractImage(uint image, uint left, uint top, uint width, uint height, out uint extracted);
    [LibraryImport(LibName, EntryPoint = "imageClone")]
    internal static partial uint CloneImage(uint image, out uint clone);
    [LibraryImport(LibName, EntryPoint = "bufferDrawImage")]
    private static partial byte DrawImageCore(uint buffer, uint image, in NativeImageDrawOptions options);
    [LibraryImport(LibName, EntryPoint = "queryPixelResolution")]
    public static partial void QueryPixelResolution(uint renderer);

    internal static uint InspectImage(ReadOnlySpan<byte> bytes, out NativeImageInfo info)
    {
        fixed (byte* data = bytes) return InspectImageCore(data, (uint)bytes.Length, out info);
    }
    internal static uint DecodeImage(ReadOnlySpan<byte> bytes, out uint image)
    {
        fixed (byte* data = bytes) return DecodeImageCore(data, (uint)bytes.Length, out image);
    }
    internal static uint CopyImagePixels(uint image, Span<byte> output, uint stride, bool bgra)
    {
        fixed (byte* data = output) return CopyImagePixelsCore(image, data, (ulong)output.Length, stride, bgra ? (byte)1 : (byte)0);
    }
    internal static uint CreateImageRgba(ReadOnlySpan<byte> pixels, uint width, uint height, uint stride, out uint image)
    {
        fixed (byte* data = pixels) return CreateImageRgbaCore(data, (ulong)pixels.Length, width, height, stride, out image);
    }
    internal static bool DrawImage(uint buffer, uint image, in NativeImageDrawOptions options) => DrawImageCore(buffer, image, in options) != 0;
}
