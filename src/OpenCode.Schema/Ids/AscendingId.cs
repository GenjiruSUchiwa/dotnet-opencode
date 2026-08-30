namespace OpenCode.Schema;

using System.Security.Cryptography;
using System.Text;

public static class AscendingId
{
    private static readonly char[] Base62Chars =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();

    public static string Generate()
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sb = new StringBuilder(24);

        // Encode 8-char timestamp
        for (int i = 7; i >= 0; i--)
        {
            sb.Append(Base62Chars[(int)((timestamp >> (i * 6)) & 0x3F)]);
        }

        // 16 chars of random entropy
        byte[] randomBytes = new byte[16];
        RandomNumberGenerator.Fill(randomBytes);
        for (int i = 0; i < 16; i++)
        {
            sb.Append(Base62Chars[randomBytes[i] % 62]);
        }

        return sb.ToString();
    }
}
