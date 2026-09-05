namespace OpenTui.Native;

using System.Runtime.InteropServices;

public enum NativeYogaEnum : uint { Direction, FlexDirection, JustifyContent, AlignContent, AlignItems, AlignSelf, PositionType, FlexWrap, Overflow, Display, BoxSizing }
public enum NativeYogaFloat : uint { Flex, Grow, Shrink, AspectRatio }
public enum NativeYogaValue : uint { Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight, Basis, Margin, Padding, Position, Gap }
public enum NativeYogaUnit : uint { Undefined, Point, Percent, Auto }
public enum NativeYogaMeasureMode : uint { Undefined, Exactly, AtMost }

/// <summary>Existing yoga.zig output-pointer ABI: six consecutive f32 values.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeYogaLayout
{
    public readonly float Left, Top, Right, Bottom, Width, Height;
}
public readonly record struct NativeYogaSize(float Width, float Height);

public static unsafe partial class OpenTuiNative
{
    [LibraryImport(LibName, EntryPoint = "textBufferViewSetTruncate")]
    internal static partial void SetTextViewTruncate(uint view, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [LibraryImport(LibName, EntryPoint = "yogaConfigCreate")]
    internal static partial nint YogaConfigCreate();
    [LibraryImport(LibName, EntryPoint = "yogaConfigFree")]
    internal static partial void YogaConfigFree(nint config);
    [LibraryImport(LibName, EntryPoint = "yogaConfigSetUseWebDefaults")]
    internal static partial void YogaConfigSetUseWebDefaults(nint config, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [LibraryImport(LibName, EntryPoint = "yogaConfigSetPointScaleFactor")]
    internal static partial void YogaConfigSetPointScaleFactor(nint config, float scale);
    [LibraryImport(LibName, EntryPoint = "yogaNodeCreateWithConfig")]
    internal static partial nint YogaNodeCreate(nint config);
    [LibraryImport(LibName, EntryPoint = "yogaNodeFree")]
    internal static partial void YogaNodeFree(nint node);
    [LibraryImport(LibName, EntryPoint = "yogaNodeRemoveAllChildren")]
    internal static partial void YogaRemoveChildren(nint node);
    [LibraryImport(LibName, EntryPoint = "yogaNodeInsertChild")]
    internal static partial void YogaInsertChild(nint node, nint child, uint index);
    [LibraryImport(LibName, EntryPoint = "yogaNodeCalculateLayout")]
    internal static partial void YogaCalculate(nint node, float width, float height, uint direction);
    [LibraryImport(LibName, EntryPoint = "yogaNodeGetComputedLayout")]
    internal static partial void YogaGetLayout(nint node, out NativeYogaLayout layout);
    [LibraryImport(LibName, EntryPoint = "yogaNodeStyleSetEnum")]
    internal static partial void YogaSetEnum(nint node, NativeYogaEnum kind, uint value);
    [LibraryImport(LibName, EntryPoint = "yogaNodeStyleSetFloat")]
    internal static partial void YogaSetFloat(nint node, NativeYogaFloat kind, float value);
    [LibraryImport(LibName, EntryPoint = "yogaNodeStyleSetValue")]
    internal static partial void YogaSetValue(nint node, NativeYogaValue kind, uint edge, NativeYogaUnit unit, float value);
    [LibraryImport(LibName, EntryPoint = "yogaNodeStyleSetBorder")]
    internal static partial void YogaSetBorder(nint node, uint edge, float value);
    [LibraryImport(LibName, EntryPoint = "yogaSetMeasureCallback")]
    internal static partial void YogaSetMeasureCallback(delegate* unmanaged[Cdecl]<nint, float, uint, float, uint, void> callback);
    [LibraryImport(LibName, EntryPoint = "yogaNodeSetMeasureFunc")]
    internal static partial void YogaSetMeasure(nint node, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [LibraryImport(LibName, EntryPoint = "yogaStoreMeasureResult")]
    internal static partial void YogaStoreMeasure(float width, float height);
    [LibraryImport(LibName, EntryPoint = "createNativeRenderable")]
    internal static partial uint CreateMeasureRenderable();
    [LibraryImport(LibName, EntryPoint = "destroyNativeRenderable")]
    internal static partial void DestroyMeasureRenderable(uint handle);
    [LibraryImport(LibName, EntryPoint = "nativeRenderableAttachYogaNode")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool AttachMeasureYoga(uint handle, nint node);
    [LibraryImport(LibName, EntryPoint = "nativeRenderableSetMeasureTarget")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool SetMeasureTarget(uint handle, uint kind, uint target);
}
