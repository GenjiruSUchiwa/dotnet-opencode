namespace OpenTui.Native;

using System.Runtime.InteropServices;

public static unsafe partial class OpenTuiNative
{
    [LibraryImport(LibName, EntryPoint = "clipboardServiceCreate")]
    private static partial uint CreateClipboardServiceCore(uint operations, uint transfers, byte* seat, uint length);
    [LibraryImport(LibName, EntryPoint = "clipboardServiceBeginShutdown")]
    internal static partial NativeClipboardShutdownStatus BeginClipboardShutdown(uint service);
    [LibraryImport(LibName, EntryPoint = "clipboardServicePollShutdown")]
    internal static partial NativeClipboardShutdownStatus PollClipboardShutdown(uint service);
    [LibraryImport(LibName, EntryPoint = "clipboardServiceDestroy")]
    internal static partial NativeClipboardDestroyStatus DestroyClipboardService(uint service);
    [LibraryImport(LibName, EntryPoint = "clipboardServiceDrain")]
    internal static partial byte DrainClipboardService(uint service);
    [LibraryImport(LibName, EntryPoint = "clipboardReadOperationStart")]
    private static partial NativeClipboardStartStatus StartClipboardReadCore(uint service, byte* request, uint length,
        NativeClipboardSelection selection, uint maxBytes, uint maxPixels, uint maxConversion, uint timeout, out uint operation);
    [LibraryImport(LibName, EntryPoint = "clipboardWriteOperationStart")]
    private static partial NativeClipboardStartStatus StartClipboardWriteCore(uint service, byte* text, uint length,
        NativeClipboardSelection selection, uint timeout, out uint operation);
    [LibraryImport(LibName, EntryPoint = "clipboardClearOperationStart")]
    internal static partial NativeClipboardStartStatus StartClipboardClear(uint service, NativeClipboardSelection selection, uint timeout, out uint operation);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationPoll")]
    internal static partial NativeClipboardOperationStatus PollClipboardOperation(uint operation);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationCancel")]
    internal static partial NativeClipboardCancelStatus CancelClipboardOperation(uint operation);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationDestroy")]
    internal static partial NativeClipboardDestroyStatus DestroyClipboardOperation(uint operation);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationResultMimeLength")]
    internal static partial NativeClipboardCopyStatus ClipboardMimeLength(uint operation, out uint length);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationResultDataLength")]
    internal static partial NativeClipboardCopyStatus ClipboardDataLength(uint operation, out uint length);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationResultDiagnosticLength")]
    internal static partial NativeClipboardCopyStatus ClipboardDiagnosticLength(uint operation, out uint length);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationResultErrorCode")]
    internal static partial NativeClipboardCopyStatus ClipboardErrorCode(uint operation, out uint code);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationResultMimeCopy")]
    private static partial NativeClipboardCopyStatus ClipboardMimeCopyCore(uint operation, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationResultDataCopy")]
    private static partial NativeClipboardCopyStatus ClipboardDataCopyCore(uint operation, byte* output, uint capacity);
    [LibraryImport(LibName, EntryPoint = "clipboardOperationResultDiagnosticCopy")]
    private static partial NativeClipboardCopyStatus ClipboardDiagnosticCopyCore(uint operation, byte* output, uint capacity);

    internal static uint CreateClipboardService(uint operations, uint transfers, ReadOnlySpan<byte> seat)
    {
        fixed (byte* bytes = seat) return CreateClipboardServiceCore(operations, transfers, bytes, (uint)seat.Length);
    }
    internal static NativeClipboardStartStatus StartClipboardRead(uint service, ReadOnlySpan<byte> request,
        NativeClipboardSelection selection, NativeClipboardOptions options, out uint operation)
    {
        fixed (byte* bytes = request) return StartClipboardReadCore(service, bytes, (uint)request.Length, selection,
            options.MaxReadBytes, options.MaxImagePixels, options.MaxConversionBytes, options.TimeoutMilliseconds, out operation);
    }
    internal static NativeClipboardStartStatus StartClipboardWrite(uint service, ReadOnlySpan<byte> text,
        NativeClipboardSelection selection, uint timeout, out uint operation)
    {
        fixed (byte* bytes = text) return StartClipboardWriteCore(service, bytes, (uint)text.Length, selection, timeout, out operation);
    }
    internal static NativeClipboardCopyStatus CopyClipboardMime(uint operation, Span<byte> output)
    {
        fixed (byte* bytes = output) return ClipboardMimeCopyCore(operation, bytes, (uint)output.Length);
    }
    internal static NativeClipboardCopyStatus CopyClipboardData(uint operation, Span<byte> output)
    {
        fixed (byte* bytes = output) return ClipboardDataCopyCore(operation, bytes, (uint)output.Length);
    }
    internal static NativeClipboardCopyStatus CopyClipboardDiagnostic(uint operation, Span<byte> output)
    {
        fixed (byte* bytes = output) return ClipboardDiagnosticCopyCore(operation, bytes, (uint)output.Length);
    }
}
