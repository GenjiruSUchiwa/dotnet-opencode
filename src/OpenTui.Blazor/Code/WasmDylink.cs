namespace OpenTui.Blazor.Code;

using System.Text;

/// <summary>Reads Emscripten dylink.0 allocation metadata without compiling or executing a module.</summary>
internal sealed record WasmDylink(int MemorySize, int MemoryAlignment, int TableSize, int TableAlignment)
{
    internal static WasmDylink Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || !bytes[..8].SequenceEqual(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 }))
            throw new InvalidDataException("Not a WebAssembly v1 module.");
        var offset = 8;
        while (offset < bytes.Length)
        {
            var id = bytes[offset++];
            var length = Leb(bytes, ref offset);
            var end = checked(offset + length);
            if (end > bytes.Length) throw new InvalidDataException("Truncated WASM section.");
            if (id == 0)
            {
                var size = Leb(bytes[..end], ref offset);
                var name = Encoding.UTF8.GetString(bytes.Slice(offset, size));
                offset += size;
                if (name == "dylink.0")
                {
                    WasmDylink? result = null;
                    while (offset < end)
                    {
                        var subsection = bytes[offset++];
                        var count = Leb(bytes[..end], ref offset);
                        var stop = checked(offset + count);
                        if (stop > end) throw new InvalidDataException("Truncated dylink section.");
                        if (subsection == 1) result = new(Leb(bytes[..stop], ref offset), Leb(bytes[..stop], ref offset),
                            Leb(bytes[..stop], ref offset), Leb(bytes[..stop], ref offset));
                        if (subsection == 2 && Leb(bytes[..stop], ref offset) != 0)
                            throw new NotSupportedException("Bundled grammars must not load additional dynamic libraries.");
                        offset = stop;
                    }
                    return result ?? throw new InvalidDataException("Missing dylink allocation metadata.");
                }
            }
            offset = end;
        }
        throw new InvalidDataException("Missing dylink.0 section.");
    }

    private static int Leb(ReadOnlySpan<byte> bytes, ref int offset)
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("Truncated LEB128.");
            var next = bytes[offset++];
            if (shift == 28 && (next & 0xf8) != 0) throw new InvalidDataException("LEB128 exceeds supported address range.");
            value |= (uint)(next & 127) << shift;
            if ((next & 128) == 0) return checked((int)value);
        }
        throw new InvalidDataException("Invalid LEB128.");
    }
}
