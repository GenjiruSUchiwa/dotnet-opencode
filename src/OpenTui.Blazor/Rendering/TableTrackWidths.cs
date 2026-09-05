namespace OpenTui.Blazor.Rendering;

using System.Numerics;

/// <summary>OpenTUI 0.5.9 TextTable's shared column fitter, not a flex layout engine.</summary>
// Port of src/renderables/text-table-width.ts and TextTable.expandColumnWidths
// (MIT, copyright 2025 opentui; existing distributed OpenTUI license retained).
internal static class TableTrackWidths
{
    internal static int[] Fit(int[] widths, int target, int minimum)
    {
        var basis = widths.Select(width => Math.Max(minimum, width)).ToArray();
        if (basis.Length == 0) return basis;
        var total = basis.Sum(width => (long)width);
        if (total <= target)
        {
            var extra = target - total;
            return basis.Select((width, index) => checked(width + (int)(extra / basis.Length) + (index < extra % basis.Length ? 1 : 0))).ToArray();
        }
        var capacity = basis.Select(width => width - minimum).ToArray();
        var growth = new int[basis.Length];
        var available = Math.Min(Math.Max(0L, target - (long)minimum * basis.Length), capacity.Sum(value => (long)value));
        if (available == 0) return basis.Select(_ => minimum).ToArray();
        var active = capacity.Select((value, index) => (Index: index, Capacity: value, Weight: Math.Sqrt(value)))
            .Where(column => column.Capacity > 0).OrderBy(column => column.Weight).ToArray();
        if (active.Length == capacity.Length && capacity.All(value => value == capacity[0]))
            return growth.Select((_, index) => checked(minimum + (int)(available / capacity.Length) + (index < available % capacity.Length ? 1 : 0))).ToArray();
        var remaining = (double)available;
        var weight = active.Sum(column => column.Weight);
        foreach (var column in active)
        {
            if (remaining / weight <= column.Weight) break;
            growth[column.Index] = column.Capacity;
            remaining -= column.Capacity;
            weight -= column.Weight;
        }
        var level = remaining / weight;
        foreach (var column in active)
            if (growth[column.Index] != column.Capacity)
                growth[column.Index] = Math.Min(column.Capacity, checked((int)Math.Floor(level * column.Weight)));
        var allocated = growth.Sum(value => (long)value);
        while (allocated > available)
        {
            var worst = -1;
            for (var index = 0; index < basis.Length; index++)
            {
                if (growth[index] == 0) continue;
                var comparison = worst == -1 ? 1 : Compare(growth[index], capacity[index], growth[worst], capacity[worst]);
                if (comparison > 0 || comparison == 0 && index > worst) worst = index;
            }
            if (worst < 0) break;
            growth[worst]--; allocated--;
        }
        while (allocated < available)
        {
            var best = -1;
            for (var index = 0; index < basis.Length; index++)
            {
                if (growth[index] >= capacity[index]) continue;
                if (best == -1 || Compare(growth[index] + 1, capacity[index], growth[best] + 1, capacity[best]) < 0) best = index;
            }
            if (best < 0) break;
            growth[best]++; allocated++;
        }
        return growth.Select(value => checked(value + minimum)).ToArray();
    }

    private static int Compare(int leftGrowth, int leftCapacity, int rightGrowth, int rightCapacity) =>
        ((BigInteger)leftGrowth * leftGrowth * rightCapacity).CompareTo((BigInteger)rightGrowth * rightGrowth * leftCapacity);
}
