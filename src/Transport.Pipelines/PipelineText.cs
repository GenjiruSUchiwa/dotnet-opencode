namespace Transport;

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

public sealed record TextRecord(string Text, bool Terminated);

/// <summary>UTF-16 record framing over a Pipe; decoding remains the framework's responsibility.</summary>
public sealed class TextRecordBuffer(long maximumCharacters = long.MaxValue, bool lfOnly = false,
    Func<Exception>? tooLarge = null) : IDisposable
{
    private readonly SequenceBuffer _bytes = new();
    private long _scanned;
    private bool _skipLf;
    public long MaximumCharacters { get; set; } = maximumCharacters;
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        var target = _bytes.GetMemory(checked(text.Length * 2)).Span;
        for (var index = 0; index < text.Length; index++) BinaryPrimitives.WriteUInt16LittleEndian(target[(index * 2)..], text[index]);
        _bytes.Commit(text.Length * 2);
    }
    public bool TryRead(out TextRecord record, bool completed = false)
    {
        record = null!;
        if (_bytes.Length == 0) return false;
        var sequence = _bytes.Sequence;
        if (_skipLf)
        {
            _skipLf = false;
            var first = new SequenceReader<byte>(sequence);
            first.TryReadLittleEndian(out short value);
            if (value == '\n') { _bytes.Consume(2); if (_bytes.Length == 0) return false; sequence = _bytes.Sequence; }
        }
        var reader = new SequenceReader<byte>(sequence.Slice(_scanned));
        while (reader.TryReadLittleEndian(out short value))
        {
            _scanned += 2;
            if (value == '\n' || (!lfOnly && value == '\r'))
            {
                var text = Decode(sequence.Slice(0, _scanned - 2));
                _bytes.Consume(_scanned);
                _scanned = 0;
                _skipLf = !lfOnly && value == '\r';
                record = new(text, true);
                return true;
            }
            if (_scanned / 2 > MaximumCharacters) throw tooLarge?.Invoke() ?? new IOException("Text record limit exceeded.");
        }
        if (!completed) return false;
        record = new(Decode(sequence), false);
        _bytes.Clear(); _scanned = 0;
        return true;
    }
    internal static string Decode(ReadOnlySequence<byte> sequence)
    {
        var chars = new char[checked((int)(sequence.Length / 2))];
        var reader = new SequenceReader<byte>(sequence);
        for (var index = 0; index < chars.Length; index++) { reader.TryReadLittleEndian(out short value); chars[index] = unchecked((char)value); }
        return new string(chars);
    }
    public void Dispose() => _bytes.Dispose();
}

/// <summary>Segmented owned text payload assembly, preserving raw UTF-16 code units.</summary>
public sealed class TextSequenceBuffer : IDisposable
{
    private readonly SequenceBuffer _bytes = new();
    public int Length => checked((int)(_bytes.Length / 2));
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        var target = _bytes.GetMemory(checked(text.Length * 2)).Span;
        for (var index = 0; index < text.Length; index++) BinaryPrimitives.WriteUInt16LittleEndian(target[(index * 2)..], text[index]);
        _bytes.Commit(text.Length * 2);
    }
    public void Clear() => _bytes.Clear();
    public override string ToString() => TextRecordBuffer.Decode(_bytes.Sequence);
    public void Dispose() => _bytes.Dispose();
}

public static class PipelineText
{
    /// <summary>Retains StreamReader's line-level decoder/exception dispatch boundary.</summary>
    public static async IAsyncEnumerable<string> DecodedLinesAsync(Stream stream, Encoding encoding,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pipe = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            var adapter = pipe.AsStream(leaveOpen: true);
            await using var adapterLifetime = adapter.ConfigureAwait(false);
            using var reader = new StreamReader(adapter, encoding, detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) yield return line;
        }
        finally { await pipe.CompleteAsync().ConfigureAwait(false); }
    }

    public static async IAsyncEnumerable<string> ChunksAsync(Stream stream, Encoding encoding, bool detectBom = true,
        bool stripInitialBom = false, bool preserveSurrogates = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pipe = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            var adapter = pipe.AsStream(leaveOpen: true);
            await using var adapterLifetime = adapter.ConfigureAwait(false);
            using var reader = new StreamReader(adapter, encoding, detectBom, bufferSize: 1024, leaveOpen: true);
            var chars = new char[4097];
            var first = true;
            while (true)
            {
                var count = await reader.ReadAsync(chars.AsMemory(0, 4096), cancellationToken).ConfigureAwait(false);
                if (count == 0) yield break;
                if (preserveSurrogates && char.IsHighSurrogate(chars[count - 1]))
                    count += await reader.ReadAsync(chars.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
                var offset = first && stripInitialBom && chars[0] == '\uFEFF' ? 1 : 0;
                first = false;
                if (count > offset) yield return new string(chars, offset, count - offset);
            }
        }
        finally { await pipe.CompleteAsync().ConfigureAwait(false); }
    }

    public static async IAsyncEnumerable<TextRecord> LinesAsync(Stream stream, Encoding encoding, bool detectBom = true,
        bool stripInitialBom = false, long maximumCharacters = long.MaxValue, Func<Exception>? tooLarge = null,
        Func<long>? remainingCharacters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var records = new TextRecordBuffer(maximumCharacters, tooLarge: tooLarge);
        await foreach (var chunk in ChunksAsync(stream, encoding, detectBom, stripInitialBom, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            records.Append(chunk);
            while (true)
            {
                records.MaximumCharacters = remainingCharacters?.Invoke() ?? maximumCharacters;
                if (!records.TryRead(out var record)) break;
                yield return record;
            }
        }
        while (records.TryRead(out var record, completed: true)) yield return record;
    }
}
