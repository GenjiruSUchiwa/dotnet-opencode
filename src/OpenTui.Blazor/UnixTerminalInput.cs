namespace OpenTui.Blazor;

using OpenTui.Native;

internal enum UnixInputStatus { WouldBlock, BudgetExhausted, EndOfStream }
internal readonly record struct UnixInputFrame(UnixInputStatus Status, int BytesRead, int ReadCalls);

/// <summary>Owns byte-to-parser delivery, not terminal modes. Call ReadFrame on the host
/// dispatcher and dispose before NativeTerminal. No threads, streams, or Console PAL.</summary>
internal sealed class UnixTerminalInput : IDisposable
{
    private readonly NativeTerminal _terminal;
    private readonly TerminalInput _parser;
    private readonly byte[] _buffer = new byte[4096];
    private bool _ended;
    private bool _disposed;

    internal UnixTerminalInput(NativeTerminal terminal, TerminalInput parser)
    {
        if (terminal.InputKind != NativeTerminalInputKind.UnixBytes) throw new PlatformNotSupportedException("UnixTerminalInput requires a Unix byte-input terminal owner.");
        _terminal = terminal;
        _parser = parser;
    }

    public UnixInputFrame ReadFrame(int byteBudget = 4096, int readBudget = 4)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readBudget);
        if (_ended) return new(UnixInputStatus.EndOfStream, 0, 0);
        var total = 0;
        var calls = 0;
        while (total < byteBudget && calls < readBudget)
        {
            calls++;
            if (!_terminal.TryReadUnixInput(_buffer.AsSpan(0, Math.Min(_buffer.Length, byteBudget - total)), out var count))
            {
                _parser.FlushEscape();
                return new(UnixInputStatus.WouldBlock, total, calls);
            }
            if (count == 0)
            {
                _ended = true;
                _parser.CompleteUnixInput();
                return new(UnixInputStatus.EndOfStream, total, calls);
            }
            _parser.FeedUnixBytes(_buffer.AsSpan(0, count));
            total += count;
        }
        // More input may be queued. Do not turn an unfinished ESC into a key merely
        // because this frame's work budget was reached.
        return new(UnixInputStatus.BudgetExhausted, total, calls);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.DiscardUnixInput();
    }
}
