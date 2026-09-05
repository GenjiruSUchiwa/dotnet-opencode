namespace OpenTui.Native;

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

/// <summary>Owns a synchronous Yoga layout transaction. Node/config pointers are not OpenTUI uint handles.</summary>
public sealed class NativeYogaTree : IDisposable
{
    private static readonly Lock CalculationGate = new();
    [ThreadStatic] private static NativeYogaTree? _calculating;
    private nint _config;
    private readonly HashSet<nint> _nodes = [];
    private readonly List<uint> _measureOwners = [];
    private readonly List<NativeTextView> _views = [];
    private readonly List<NativeEditor> _editors = [];
    private readonly List<IDisposable> _editorLeases = [];
    private readonly Dictionary<nint, nint> _parents = [];
    private readonly Dictionary<nint, uint> _childCounts = [];
    private readonly HashSet<nint> _leaves = [];
    private readonly Dictionary<nint, Func<float, NativeYogaMeasureMode, float, NativeYogaMeasureMode, NativeYogaSize>> _measures = [];
    private ExceptionDispatchInfo? _error;

    public NativeYogaTree()
    {
        _config = OpenTuiNative.YogaConfigCreate();
        if (_config == 0) throw new InvalidOperationException("Yoga config allocation failed.");
        try
        {
            OpenTuiNative.YogaConfigSetUseWebDefaults(_config, false);
            OpenTuiNative.YogaConfigSetPointScaleFactor(_config, 1);
        }
        catch { Dispose(); throw; }
    }

    public nint CreateNode()
    {
        ObjectDisposedException.ThrowIf(_config == 0, this);
        if (ReferenceEquals(_calculating, this)) throw new InvalidOperationException("Yoga measure callbacks cannot mutate the calculating tree.");
        var node = OpenTuiNative.YogaNodeCreate(_config);
        if (node == 0) throw new InvalidOperationException("Yoga node allocation failed.");
        _nodes.Add(node);
        return node;
    }
    public void Insert(nint parent, nint child, uint index)
    {
        Require(parent); Require(child);
        if (_leaves.Contains(parent)) throw new InvalidOperationException("A Yoga measured leaf cannot have children.");
        var count = _childCounts.GetValueOrDefault(parent);
        if (index > count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_parents.ContainsKey(child)) throw new InvalidOperationException("Yoga node already has a parent in this transaction.");
        for (var ancestor = parent; ancestor != 0; ancestor = _parents.GetValueOrDefault(ancestor))
            if (ancestor == child) throw new InvalidOperationException("Yoga tree cannot contain a cycle.");
        OpenTuiNative.YogaInsertChild(parent, child, index);
        _parents.Add(child, parent);
        _childCounts[parent] = count + 1;
    }
    public void SetEnum(nint node, NativeYogaEnum kind, uint value)
    {
        Require(node);
        var maximum = kind switch
        {
            NativeYogaEnum.Direction or NativeYogaEnum.PositionType or NativeYogaEnum.FlexWrap or NativeYogaEnum.Overflow or NativeYogaEnum.Display => 2u,
            NativeYogaEnum.FlexDirection => 3u, NativeYogaEnum.JustifyContent => 5u,
            NativeYogaEnum.AlignContent or NativeYogaEnum.AlignItems or NativeYogaEnum.AlignSelf => 8u,
            NativeYogaEnum.BoxSizing => 1u, _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
        OpenTuiNative.YogaSetEnum(node, kind, value);
    }
    public void SetFloat(nint node, NativeYogaFloat kind, float value)
    {
        Require(node);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
        OpenTuiNative.YogaSetFloat(node, kind, value);
    }
    public void SetValue(nint node, NativeYogaValue kind, NativeYogaUnit unit, float value = 0, uint edge = 0)
    {
        Require(node);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(unit)) throw new ArgumentOutOfRangeException(nameof(unit));
        if (edge > (kind == NativeYogaValue.Gap ? 2u : 8u)) throw new ArgumentOutOfRangeException(nameof(edge));
        if (float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
        OpenTuiNative.YogaSetValue(node, kind, edge, unit, value);
    }
    public void SetBorder(nint node, uint edge, float value)
    {
        Require(node);
        if (edge > 8) throw new ArgumentOutOfRangeException(nameof(edge));
        OpenTuiNative.YogaSetBorder(node, edge, value);
    }
    public NativeYogaLayout Layout(nint node) { Require(node, mutate: false); OpenTuiNative.YogaGetLayout(node, out var result); return result; }

    private void Require(nint node, bool mutate = true)
    {
        ObjectDisposedException.ThrowIf(_config == 0, this);
        if (mutate && ReferenceEquals(_calculating, this)) throw new InvalidOperationException("Yoga measure callbacks cannot mutate the calculating tree.");
        if (!_nodes.Contains(node)) throw new ArgumentException("Yoga pointer does not belong to this live transaction.", nameof(node));
    }

    /// <summary>Yoga measures text entirely natively; the view must remain alive until this tree is disposed.</summary>
    public void MeasureText(nint node, NativeTextView view)
    {
        Require(node);
        if (_childCounts.GetValueOrDefault(node) != 0 || !_leaves.Add(node)) throw new InvalidOperationException("Measure target requires an unmeasured Yoga leaf.");
        var handle = OpenTuiNative.CreateMeasureRenderable();
        if (handle == 0) throw new InvalidOperationException("Native measure-target allocation failed.");
        _measureOwners.Add(handle);
        if (!OpenTuiNative.AttachMeasureYoga(handle, node) || !OpenTuiNative.SetMeasureTarget(handle, 1, view.ViewHandle))
            throw new InvalidOperationException("Native text measure target could not be attached to Yoga.");
        _views.Add(view);
    }

    public void Measure(nint node, Func<float, NativeYogaMeasureMode, float, NativeYogaMeasureMode, NativeYogaSize> measure)
    {
        Require(node);
        if (_childCounts.GetValueOrDefault(node) != 0 || !_leaves.Add(node)) throw new InvalidOperationException("Measure callback requires an unmeasured Yoga leaf.");
        _measures.Add(node, measure);
        OpenTuiNative.YogaSetMeasure(node, true);
    }

    public void MeasureEditor(nint node, NativeEditor editor)
    {
        Require(node);
        if (_childCounts.GetValueOrDefault(node) != 0 || !_leaves.Add(node)) throw new InvalidOperationException("Measure target requires an unmeasured Yoga leaf.");
        _editorLeases.Add(editor.BorrowForMeasure());
        var handle = OpenTuiNative.CreateMeasureRenderable();
        if (handle == 0) throw new InvalidOperationException("Native measure-target allocation failed.");
        _measureOwners.Add(handle);
        if (!OpenTuiNative.AttachMeasureYoga(handle, node) || !OpenTuiNative.SetMeasureTarget(handle, 2, editor.ViewHandle))
            throw new InvalidOperationException("Native editor measure target could not be attached to Yoga.");
        _editors.Add(editor);
    }

    public unsafe void Calculate(nint root, float width = float.NaN, float height = float.NaN)
    {
        ObjectDisposedException.ThrowIf(_config == 0, this);
        Require(root);
        foreach (var view in _views) _ = view.ViewHandle;
        foreach (var editor in _editors) _ = editor.ViewHandle;
        lock (CalculationGate)
        {
            if (_calculating is not null) throw new InvalidOperationException("A Yoga measure callback cannot recursively calculate layout.");
            _error = null;
            _calculating = this;
            try
            {
                // One static unmanaged trampoline; per-tree delegates
                // remain strongly referenced until synchronous calculation returns.
                OpenTuiNative.YogaSetMeasureCallback(&MeasureCallback);
                OpenTuiNative.YogaCalculate(root, width, height, 1);
            }
            finally { OpenTuiNative.YogaSetMeasureCallback(null); _calculating = null; }
            _error?.Throw();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MeasureCallback(nint node, float width, uint widthMode, float height, uint heightMode)
    {
        var tree = _calculating;
        try
        {
            var size = tree is not null && tree._measures.TryGetValue(node, out var measure)
                ? measure(width, (NativeYogaMeasureMode)widthMode, height, (NativeYogaMeasureMode)heightMode) : new NativeYogaSize(0, 0);
            if (!float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width < 0 || size.Height < 0)
                throw new InvalidOperationException("Managed Yoga measurement must return finite nonnegative dimensions.");
            OpenTuiNative.YogaStoreMeasure(size.Width, size.Height);
        }
        catch (Exception exception)
        {
            if (tree is not null) tree._error ??= ExceptionDispatchInfo.Capture(exception);
            // Exceptions cannot cross the C frame; report after Calculate returns.
            OpenTuiNative.YogaStoreMeasure(0, 0);
        }
    }

    public void Dispose()
    {
        if (_config == 0) return;
        if (ReferenceEquals(_calculating, this)) throw new InvalidOperationException("Yoga tree cannot be disposed from its measure callback.");
        // Measure renderables borrow Yoga nodes and native text views. Detach first.
        foreach (var handle in _measureOwners) OpenTuiNative.DestroyMeasureRenderable(handle);
        foreach (var node in _nodes) OpenTuiNative.YogaRemoveChildren(node);
        foreach (var node in _nodes) OpenTuiNative.YogaNodeFree(node);
        OpenTuiNative.YogaConfigFree(_config);
        _config = 0;
        foreach (var lease in _editorLeases) lease.Dispose();
        _editorLeases.Clear();
        _measureOwners.Clear(); _nodes.Clear(); _measures.Clear(); _parents.Clear(); _views.Clear(); _editors.Clear(); _childCounts.Clear(); _leaves.Clear();
    }
}
