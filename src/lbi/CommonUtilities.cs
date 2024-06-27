using System.IO;
using System.Security.Cryptography;

public class Utilities
{
    public static string SHA1Hash(string filePath)
    {
        return Hash(filePath, SHA1.Create());
    }

    public static string SHA256Hash(string filePath)
    {
        return Hash(filePath, SHA256.Create());
    }

    public static string Hash(string filePath, HashAlgorithm hash)
    {
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (hash)
        {
            var result = hash.ComputeHash(stream);
            return ByteArrayToHexString(result);
        }
    }

    public static string ByteArrayToHexString(byte[] bytes, int digits = 0)
    {
        if (digits == 0)
        {
            digits = bytes.Length * 2;
        }

        char[] c = new char[digits];
        byte b;
        for (int i = 0; i < digits / 2; i++)
        {
            b = ((byte)(bytes[i] >> 4));
            c[i * 2] = (char)(b > 9 ? b + 87 : b + 0x30);
            b = ((byte)(bytes[i] & 0xF));
            c[i * 2 + 1] = (char)(b > 9 ? b + 87 : b + 0x30);
        }

        return new string(c);
    }
}