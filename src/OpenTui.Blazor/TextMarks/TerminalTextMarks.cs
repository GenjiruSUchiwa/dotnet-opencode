namespace OpenTui.Blazor.TextMarks;

using System.Collections.Immutable;

public sealed record TerminalTextMarksSnapshot(ImmutableArray<TerminalExtmark> Marks, int NextId);

/// <summary>Managed port of OpenTUI 0.5.9 ExtmarksController's interval/cursor rules.
/// The owner performs the text edit and supplies native-derived display offsets.</summary>
public sealed class TerminalTextMarks
{
    private readonly Dictionary<int, TerminalExtmark> _marks = [];
    private readonly Dictionary<string, int> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<int>> _byType = [];
    private int _nextId = 1;
    public IReadOnlyList<TerminalExtmark> All => _marks.Values.ToArray();
    public TerminalTextMarksSnapshot Snapshot() => new(_marks.Values.ToImmutableArray(), _nextId);

    public int RegisterType(string name)
    {
        if (_types.TryGetValue(name, out var type)) return type;
        _types[name] = type = _types.Count + 1;
        return type;
    }

    public int Create(int start, int end, bool virtualText = false, int typeId = 0, string? styleKey = null, int priority = 0)
    {
        if (start < 0 || end < start) throw new ArgumentOutOfRangeException(nameof(start));
        var id = _nextId++;
        Put(new(id, start, end, virtualText, typeId, styleKey, priority));
        return id;
    }

    public TerminalExtmark? Get(int id) => _marks.GetValueOrDefault(id);
    public void Update(TerminalExtmark mark)
    {
        if (!_marks.ContainsKey(mark.Id)) throw new ArgumentException("Unknown mark identity.", nameof(mark));
        _byType.GetValueOrDefault(_marks[mark.Id].TypeId)?.Remove(mark.Id);
        Put(mark);
    }
    public IReadOnlyList<TerminalExtmark> At(int offset) => _marks.Values.Where(mark => offset >= mark.Start && offset < mark.End).ToArray();
    public IReadOnlyList<TerminalExtmark> ForType(int typeId) => _byType.TryGetValue(typeId, out var ids)
        ? ids.Select(id => _marks[id]).ToArray() : [];
    public TerminalExtmark? VirtualAt(int offset) => _marks.Values.FirstOrDefault(mark => mark.Virtual && offset >= mark.Start && offset < mark.End);

    public bool Delete(int id)
    {
        if (!_marks.Remove(id, out var mark)) return false;
        _byType.GetValueOrDefault(mark.TypeId)?.Remove(id);
        return true;
    }

    public void Clear() { _marks.Clear(); _byType.Clear(); }

    public void Restore(TerminalTextMarksSnapshot snapshot)
    {
        Clear();
        foreach (var mark in snapshot.Marks) Put(mark);
        _nextId = snapshot.NextId;
    }

    public void AdjustInsertion(int offset, int length)
    {
        if (length <= 0) return;
        foreach (var mark in _marks.Values.ToArray())
        {
            if (mark.Start >= offset) _marks[mark.Id] = mark with { Start = mark.Start + length, End = mark.End + length };
            else if (mark.End > offset) _marks[mark.Id] = mark with { End = mark.End + length };
        }
    }

    public void AdjustDeletion(int offset, int length)
    {
        if (length <= 0) return;
        var end = checked(offset + length);
        foreach (var mark in _marks.Values.ToArray())
        {
            if (mark.End <= offset) continue;
            if (mark.Start >= end) { _marks[mark.Id] = mark with { Start = mark.Start - length, End = mark.End - length }; continue; }
            if (mark.Start >= offset && mark.End <= end) { Delete(mark.Id); continue; }
            if (mark.Start < offset && mark.End > end) { _marks[mark.Id] = mark with { End = mark.End - length }; continue; }
            if (mark.Start < offset && mark.End > offset)
            {
                _marks[mark.Id] = mark with { End = mark.End - (Math.Min(mark.End, end) - offset) };
                continue;
            }
            if (mark.Start < end && mark.End > end) _marks[mark.Id] = mark with { Start = offset, End = mark.End - length };
        }
    }

    public TerminalMarkDeletion? AtomicDeletion(int cursor, bool backward, bool hasSelection)
    {
        if (hasSelection || backward && cursor == 0) return null;
        var mark = VirtualAt(backward ? cursor - 1 : cursor);
        return mark is not null && cursor == (backward ? mark.End : mark.Start)
            ? new(mark.Start, mark.End - mark.Start) : null;
    }

    public int MoveCursor(int current, int target, TerminalMarkMotion motion, bool hasSelection)
    {
        if (hasSelection || motion == TerminalMarkMotion.Direct) return target;
        if (motion == TerminalMarkMotion.Left)
        {
            var mark = VirtualAt(current - 1);
            return mark is not null && current >= mark.End ? SetCursor(current, Math.Max(0, mark.Start - 1)) : target;
        }
        if (motion == TerminalMarkMotion.Right)
        {
            var mark = VirtualAt(current + 1);
            return mark is not null && current <= mark.Start ? SetCursor(current, mark.End) : target;
        }
        if (motion is TerminalMarkMotion.Up or TerminalMarkMotion.Down)
        {
            var mark = VirtualAt(target);
            if (mark is null) return target;
            if (target - mark.Start >= mark.End - target) return mark.End;
            var before = Math.Max(0, mark.Start - 1);
            return motion == TerminalMarkMotion.Down && before <= current ? mark.End : before;
        }
        return SetCursor(current, target);
    }

    private int SetCursor(int current, int target)
    {
        if (target > current)
        {
            var mark = VirtualAt(target);
            return mark is not null && current <= mark.Start ? mark.End : target;
        }
        var previous = _marks.Values.FirstOrDefault(mark => mark.Virtual && current >= mark.End && target < mark.End && target >= mark.Start);
        return previous is null ? target : Math.Max(0, previous.Start - 1);
    }

    private void Put(TerminalExtmark mark)
    {
        _marks[mark.Id] = mark;
        if (!_byType.TryGetValue(mark.TypeId, out var ids)) _byType[mark.TypeId] = ids = [];
        ids.Add(mark.Id);
    }
}
