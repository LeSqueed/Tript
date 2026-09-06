using System.Security.Cryptography;
using System.Text;

namespace Tript.Core;

public static class Ulid
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        var buf = new byte[16];
        RandomNumberGenerator.Fill(buf.AsSpan(6, 10));
        var ts = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFFFFFFL);
        buf[0] = (byte)(ts >> 40);
        buf[1] = (byte)(ts >> 32);
        buf[2] = (byte)(ts >> 24);
        buf[3] = (byte)(ts >> 16);
        buf[4] = (byte)(ts >> 8);
        buf[5] = (byte)ts;
        return EncodeCrockford(buf);
    }

    public static string Derive(string canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalKey);
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey));
        var buf = new byte[16];
        Buffer.BlockCopy(sum, 0, buf, 0, 16);
        return EncodeCrockford(buf);
    }

    private static string EncodeCrockford(byte[] buf)
    {
        var chars = new char[26];
        for (var i = 0; i < 26; i++)
        {
            var value = 0;
            for (var j = 0; j < 5; j++)
            {
                var pos = i * 5 + j;
                if (pos >= 2)
                {
                    var bidx = (pos - 2) / 8;
                    var bit = (buf[bidx] >> (7 - (pos - 2) % 8)) & 1;
                    value = (value << 1) | bit;
                }
                else
                {
                    value <<= 1;
                }
            }
            chars[i] = Crockford[value];
        }
        return new string(chars);
    }
}
