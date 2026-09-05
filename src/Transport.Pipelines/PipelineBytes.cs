namespace Transport;

using System.Buffers;
using System.IO.Pipelines;

public static class PipelineBytes
{
    public static async Task<byte[]> CollectAsync(Stream stream, long maximumBytes, Func<Exception> tooLarge,
        CancellationToken cancellationToken, ReadOnlyMemory<byte> prefix = default, bool leaveOpen = true)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: leaveOpen));
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                try
                {
                    if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);
                    var length = checked(prefix.Length + buffer.Length);
                    if (length > maximumBytes) throw tooLarge();
                    if (!result.IsCompleted) continue;
                    var bytes = new byte[checked((int)length)];
                    prefix.Span.CopyTo(bytes);
                    buffer.CopyTo(bytes.AsSpan(prefix.Length));
                    return bytes;
                }
                finally { reader.AdvanceTo(buffer.Start, buffer.End); }
            }
        }
        finally { await reader.CompleteAsync().ConfigureAwait(false); }
    }
}
