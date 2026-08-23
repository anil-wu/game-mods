using System.Security.Cryptography;

namespace RelayServer.Allocation;

/// <summary>6 位房间码，去除易混淆字符（0/O/1/I/L）。</summary>
public static class JoinCode
{
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // 31 字符

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        var chars = new char[6];
        for (int i = 0; i < 6; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }
}
