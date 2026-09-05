namespace OpenTui.Blazor.Components;

/// <summary>Source border character roles, in the existing native bufferDrawBox ABI order.</summary>
public sealed record TuiBorderCharacters(uint TopLeft = 0, uint TopRight = 0, uint BottomLeft = 0, uint BottomRight = 0,
    uint Horizontal = ' ', uint Vertical = 0, uint TopT = 0, uint BottomT = 0, uint LeftT = 0, uint RightT = 0, uint Cross = 0)
{
    // Source EmptyBorder uses empty strings except for horizontal space.
    // codePointAt(0) on "" becomes zero in the source Uint32Array, not U+0020.
    public static TuiBorderCharacters Empty { get; } = new();
    internal uint[] ToCodepoints()
    {
        uint[] values = [TopLeft, TopRight, BottomLeft, BottomRight, Horizontal, Vertical, TopT, BottomT, LeftT, RightT, Cross];
        if (values.Any(value => value > 0x10ffff || value is >= 0xd800 and <= 0xdfff))
            throw new InvalidOperationException("Border characters must be Unicode scalar values.");
        return values;
    }
}
