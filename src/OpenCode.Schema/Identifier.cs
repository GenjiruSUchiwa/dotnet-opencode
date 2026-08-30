namespace OpenCode.Schema;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// 1:1 idiomatic C# port of packages/schema/src/identifier.ts
/// </summary>
public static class Identifier
{
    private const int Length = 26;
    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static readonly object Lock = new();
    private static long _lastTimestamp;
    private static long _counter;

    public static string Ascending() => Create(descending: false);

    public static string Descending() => Create(descending: true);

    public static string Create(bool descending, long? timestamp = null)
    {
        long ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        long counter;
        lock (Lock)
        {
            if (ts != _lastTimestamp)
            {
                _lastTimestamp = ts;
                _counter = 0;
            }
            _counter++;
            counter = _counter;
        }

        // const current = BigInt(timestamp) * 0x1000n + BigInt(counter)
        ulong current = ((ulong)ts * 0x1000UL) + (ulong)counter;
        ulong value = descending ? ~current : current;

        // 6 bytes (12 hex chars) for the timestamp + counter prefix
        var timeBuilder = new StringBuilder(12);
        for (int index = 0; index < 6; index++)
        {
            byte b = (byte)((value >> (40 - (8 * index))) & 0xFF);
            timeBuilder.Append(b.ToString("x2"));
        }

        // 14 random characters from Chars
        byte[] randomBytes = new byte[Length - 12];
        RandomNumberGenerator.Fill(randomBytes);

        var randomBuilder = new StringBuilder(Length - 12);
        for (int i = 0; i < randomBytes.Length; i++)
        {
            randomBuilder.Append(Chars[randomBytes[i] % 62]);
        }

        return timeBuilder.ToString() + randomBuilder.ToString();
    }
}
