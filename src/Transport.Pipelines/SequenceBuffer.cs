namespace Transport;

using System.Buffers;
using System.IO.Pipelines;

/// <summary>Single-owner segmented assembly. Borrowed sequences expire on the next mutation/disposal.</summary>
public sealed class SequenceBuffer : IDisposable
{
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
    private ReadOnlySequence<byte> _sequence;
    private bool _reading;
    public long Length { get; private set; }
    public ReadOnlySequence<byte> Sequence
    {
        get
        {
            if (!_reading) { if (!_pipe.Reader.TryRead(out var result)) return ReadOnlySequence<byte>.Empty; _sequence = result.Buffer; _reading = true; }
            return _sequence;
        }
    }
    public Memory<byte> GetMemory(int size)
    {
        Release();
        return _pipe.Writer.GetMemory(size);
    }
    public void Commit(int count)
    {
        _pipe.Writer.Advance(count);
        Length = checked(Length + count);
        // No concurrent consumer: automatic writer pausing would deadlock a large message.
        var flush = _pipe.Writer.FlushAsync();
        System.Diagnostics.Debug.Assert(flush.IsCompleted, "The single-owner pipe has writer pausing disabled.");
        flush.GetAwaiter().GetResult();
    }
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        bytes.CopyTo(GetMemory(bytes.Length).Span);
        Commit(bytes.Length);
    }
    public void Consume(long count)
    {
        var sequence = Sequence;
        var position = sequence.GetPosition(count);
        _pipe.Reader.AdvanceTo(position, position);
        _reading = false;
        Length -= count;
    }
    public byte[] ToArray() => Sequence.ToArray();
    public void Clear() { if (Length > 0) Consume(Length); }
    private void Release()
    {
        if (!_reading) return;
        _pipe.Reader.AdvanceTo(_sequence.Start, _sequence.End);
        _reading = false;
    }
    public void Dispose() { Release(); _pipe.Writer.Complete(); _pipe.Reader.Complete(); }
}
