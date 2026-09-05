namespace OpenTui.Native;

public enum NativeClipboardSelection : byte { Clipboard, Primary }
public enum NativeClipboardOperationStatus : byte { Pending, Read, Empty, Written, Cleared, Unsupported, Cancelled, TimedOut, LimitExceeded, Failed, InvalidHandle }
public enum NativeClipboardStartStatus : byte { Ok, InvalidService, ShuttingDown, LimitExceeded, InvalidArgument, OutOfMemory }
public enum NativeClipboardCancelStatus : byte { Requested, AlreadyTerminal, InvalidHandle }
public enum NativeClipboardCopyStatus : byte { Ok, BufferTooSmall, InvalidHandle, InvalidState, InvalidArgument }
public enum NativeClipboardDestroyStatus : byte { Destroyed, NotReady, InvalidHandle }
public enum NativeClipboardShutdownStatus : byte { Pending, Ready, InvalidHandle }

public sealed record NativeClipboardOptions(
    uint TimeoutMilliseconds = 1000,
    uint MaxReadBytes = 8 * 1024 * 1024,
    uint MaxWriteBytes = 8 * 1024 * 1024,
    uint MaxImagePixels = 64 * 1024 * 1024,
    uint MaxConversionBytes = 512 * 1024 * 1024,
    uint MaxConcurrentOperations = 16,
    uint MaxProviderTransfers = 16,
    string? WaylandSeat = null);

/// <summary>Owned copied bytes. No native result storage or borrowed pointers escape.</summary>
public sealed record NativeClipboardResult(NativeClipboardOperationStatus Status, string? MimeType = null,
    ReadOnlyMemory<byte> Bytes = default, uint? ErrorCode = null, string? Diagnostic = null);
