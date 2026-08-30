namespace OpenCode.Cli.Tui;

using System.Text;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[..2], 16);
        byte g = Convert.ToByte(hex[2..4], 16);
        byte b = Convert.ToByte(hex[4..6], 16);
        return new RgbColor(r, g, b);
    }

    public static RgbColor Black => new(10, 10, 10);
    public static RgbColor CardBg => new(30, 30, 30);
    public static RgbColor BottomBarBg => new(20, 20, 20);
    public static RgbColor White => new(238, 238, 238);
    public static RgbColor PureWhite => new(255, 255, 255);
    public static RgbColor Gray => new(128, 128, 128);
    public static RgbColor DarkGray => new(67, 67, 67);
    public static RgbColor Charcoal => new(40, 40, 40);
    public static RgbColor DotnetBlurple => new(123, 97, 255); // #7B61FF
    public static RgbColor AccentBlue => new(86, 156, 245);   // #569CF5
    public static RgbColor SuccessGreen => new(127, 216, 143); // #7FD88F
}

public sealed class TerminalCanvas
{
    private struct Cell
    {
        public char Character;
        public RgbColor Fg;
        public RgbColor Bg;
        public bool Bold;
    }

    private int _width;
    private int _height;
    private Cell[,] _buffer;

    public int Width => _width;
    public int Height => _height;

    public TerminalCanvas(int width, int height)
    {
        _width = Math.Max(40, width);
        _height = Math.Max(12, height);
        _buffer = new Cell[_height, _width];
        Clear(RgbColor.Black);
    }

    public void Resize(int width, int height)
    {
        _width = Math.Max(40, width);
        _height = Math.Max(12, height);
        _buffer = new Cell[_height, _width];
        Clear(RgbColor.Black);
    }

    public void Clear(RgbColor bg)
    {
        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                _buffer[y, x] = new Cell
                {
                    Character = ' ',
                    Fg = RgbColor.White,
                    Bg = bg,
                    Bold = false
                };
            }
        }
    }

    public void DrawChar(int x, int y, char c, RgbColor fg, RgbColor? bg = null, bool bold = false)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height) return;
        _buffer[y, x] = new Cell
        {
            Character = c,
            Fg = fg,
            Bg = bg ?? _buffer[y, x].Bg,
            Bold = bold
        };
    }

    public void DrawString(int x, int y, string text, RgbColor fg, RgbColor? bg = null, bool bold = false, int? maxWidth = null)
    {
        if (y < 0 || y >= _height) return;

        int limit = maxWidth.HasValue ? Math.Min(text.Length, maxWidth.Value) : text.Length;
        for (int i = 0; i < limit; i++)
        {
            if (x + i >= _width) break;
            DrawChar(x + i, y, text[i], fg, bg, bold);
        }
    }

    public void FillRect(int x, int y, int width, int height, RgbColor bg)
    {
        for (int row = y; row < y + height; row++)
        {
            for (int col = x; col < x + width; col++)
            {
                if (col >= 0 && col < _width && row >= 0 && row < _height)
                {
                    _buffer[row, col].Bg = bg;
                    _buffer[row, col].Character = ' ';
                }
            }
        }
    }

    public void Flush()
    {
        var sb = new StringBuilder(_width * _height * 15);
        // Hide cursor and position at (1,1)
        sb.Append("\x1b[?25l\x1b[H");

        RgbColor? lastFg = null;
        RgbColor? lastBg = null;
        bool lastBold = false;

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                var cell = _buffer[y, x];

                if (lastFg != cell.Fg)
                {
                    sb.Append($"\x1b[38;2;{cell.Fg.R};{cell.Fg.G};{cell.Fg.B}m");
                    lastFg = cell.Fg;
                }

                if (lastBg != cell.Bg)
                {
                    sb.Append($"\x1b[48;2;{cell.Bg.R};{cell.Bg.G};{cell.Bg.B}m");
                    lastBg = cell.Bg;
                }

                if (lastBold != cell.Bold)
                {
                    sb.Append(cell.Bold ? "\x1b[1m" : "\x1b[22m");
                    lastBold = cell.Bold;
                }

                sb.Append(cell.Character);
            }
            if (y < _height - 1)
            {
                sb.Append('\n');
            }
        }

        sb.Append("\x1b[0m");
        Console.Write(sb.ToString());
    }
}
