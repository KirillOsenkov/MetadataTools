using System;

namespace GuiLabs.Utilities;

internal static class ExtensionMethods
{
    public static string ToHexString(this byte[] bytes, char separator = ' ')
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        const int multiplier = 3;
        int digits = bytes.Length * multiplier;

        char[] c = new char[digits - 1];
        byte b;
        for (int i = 0; i < digits / multiplier; i++)
        {
            b = ((byte)(bytes[i] >> 4));
            c[i * multiplier] = (char)(b > 9 ? b + 55 : b + 0x30);
            b = ((byte)(bytes[i] & 0xF));
            c[i * multiplier + 1] = (char)(b > 9 ? b + 55 : b + 0x30);
            int index = i * 3 + 2;
            if (index < digits - 1)
            {
                c[i * 3 + 2] = separator;
            }
        }

        return new string(c);
    }

    public static string ReadZeroTerminatedString(this byte[] bytes)
    {
        int read = 0;
        int length = bytes.Length;
        var buffer = new char[length];
        while (read < length)
        {
            var current = bytes[read];
            if (current == 0)
            {
                break;
            }

            buffer[read++] = (char)current;
        }

        return new string(buffer, 0, read);
    }
}
