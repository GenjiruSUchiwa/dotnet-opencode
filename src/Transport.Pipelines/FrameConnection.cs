namespace Transport;

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

/// <summary>A stream-owned, four-byte big-endian length framing connection.</summary>
public sealed class FrameConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _reading = new(1);
    private readonly SemaphoreSlim _writing = new(1);
    private int _disposed;
    public FrameConnection(Stream stream)
    {
        _stream = stream;
        _reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        _writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }
    public async Task<byte[]> ReadAsync(int maximumBytes, Func<Exception> tooLarge, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await _reading.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var result = await _reader.ReadAsync(lifetime.Token).ConfigureAwait(false);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                try
                {
                    if (result.IsCanceled) throw new OperationCanceledException(lifetime.Token);
                    var reader = new SequenceReader<byte>(buffer);
                    if (reader.TryReadBigEndian(out int signed))
                    {
                        var length = unchecked((uint)signed);
                        if (length > maximumBytes) throw tooLarge();
                        if (reader.Remaining >= length)
                        {
                            var bytes = buffer.Slice(reader.Position, length).ToArray();
                            consumed = buffer.GetPosition(4L + length);
                            return bytes;
                        }
                    }
                    if (result.IsCompleted) throw new EndOfStreamException("Stream ended before a complete length-prefixed frame.");
                }
                finally { _reader.AdvanceTo(consumed, consumed.Equals(buffer.Start) ? buffer.End : consumed); }
            }
        }
        finally { _reading.Release(); }
    }
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await _writing.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            BinaryPrimitives.WriteUInt32BigEndian(_writer.GetSpan(4), checked((uint)bytes.Length));
            _writer.Advance(4);
            _writer.Write(bytes.Span);
            var result = await _writer.FlushAsync(lifetime.Token).ConfigureAwait(false);
            if (result.IsCanceled) throw new OperationCanceledException(lifetime.Token);
        }
        catch (Exception error)
        {
            // A failed dispatched write must never be retried by a later flush.
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _writer.CompleteAsync(error).ConfigureAwait(false);
            throw;
        }
        finally { _writing.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try
            {
                // Shutdown has already cancelled _lifetime; completion must still
                // join the active reader/writer before releasing their resources.
                await _reading.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await _reader.CompleteAsync().ConfigureAwait(false); }
                finally { _reading.Release(); }
            }
            finally
            {
                await _writing.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                // StreamPipeWriter completion normally flushes. Discard any
                // interrupted frame, including when stream disposal failed.
                try { await _writer.CompleteAsync(new OperationCanceledException()).ConfigureAwait(false); }
                finally { _writing.Release(); _lifetime.Dispose(); }
            }
        }
    }
}
