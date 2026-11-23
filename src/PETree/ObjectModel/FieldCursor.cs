namespace GuiLabs.FileFormat;

/// <summary>
/// Reads consecutive fields from a buffer. With a target node each field
/// read is also added to the node as a child, so the same code can first
/// measure a variable-length structure and then build its nodes.
/// </summary>
public class FieldCursor
{
    public FieldCursor(ByteBuffer buffer, int position, Node target = null)
    {
        Buffer = buffer;
        Position = position;
        Target = target;
    }

    public ByteBuffer Buffer { get; }
    public int Position { get; private set; }
    public Node Target { get; }

    /// <summary>
    /// Set when a read went past the end of the buffer. Reads return 0 from then on.
    /// </summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// A signed integer in the .NET native format encoding (1 to 5 bytes).
    /// </summary>
    public int CompressedInt32(string name)
    {
        int position = Position;
        int length = MeasureCompressed(Buffer, position);
        if (length == 0 || !Take(name, length))
        {
            Failed = true;
            return 0;
        }

        return ReadCompressedInt32(Buffer, position, length);
    }

    /// <summary>
    /// Byte length of a native format integer at the offset, or 0 past the end.
    /// </summary>
    public static int MeasureCompressed(ByteBuffer buffer, int offset)
    {
        if (offset < 0 || offset >= buffer.Length)
        {
            return 0;
        }

        byte first = buffer.ReadByte(offset);
        int length =
            (first & 1) == 0 ? 1 :
            (first & 2) == 0 ? 2 :
            (first & 4) == 0 ? 3 :
            (first & 8) == 0 ? 4 : 5;
        return (long)offset + length <= buffer.Length ? length : 0;
    }

    public static int ReadCompressedInt32(ByteBuffer buffer, int offset, int length)
    {
        if (length == 5)
        {
            return buffer.ReadInt32(offset + 1);
        }

        int raw = 0;
        for (int i = 0; i < length; i++)
        {
            raw |= buffer.ReadByte(offset + i) << (8 * i);
        }

        // Sign-extend from the top bit of the last byte read
        int shift = 32 - 8 * length;
        return (raw << shift) >> (shift + length);
    }

    public static uint ReadCompressedUInt32(ByteBuffer buffer, int offset, int length)
    {
        if (length == 5)
        {
            return buffer.ReadUInt32(offset + 1);
        }

        uint raw = 0;
        for (int i = 0; i < length; i++)
        {
            raw |= (uint)buffer.ReadByte(offset + i) << (8 * i);
        }

        return raw >> length;
    }

    /// <summary>
    /// Offset that reads must not go past. Defaults to the end of the buffer.
    /// </summary>
    public long Limit { get; set; } = long.MaxValue;

    public int Available => (int)System.Math.Max(0, System.Math.Min(Limit, Buffer.Length) - Position);

    /// <summary>
    /// A UTF-16 string with a zero terminator.
    /// </summary>
    public string Utf16String(string name)
    {
        int position = Position;
        int end = position;
        long limit = System.Math.Min(Limit, Buffer.Length);
        while (end + 2 <= limit && Buffer.ReadUInt16(end) != 0)
        {
            end += 2;
        }

        if (end + 2 > limit)
        {
            Failed = true;
            return null;
        }

        string value = System.Text.Encoding.Unicode.GetString(Buffer.ReadBytes(position, end - position));
        int size = end + 2 - position;
        Target?.AddBytes(size, $"{name}: {value}");
        Position += size;
        return value;
    }

    /// <summary>
    /// Adds padding up to the next multiple of the alignment, counted from the origin offset.
    /// </summary>
    public void Align(int alignment, int origin)
    {
        int misalignment = (Position - origin) % alignment;
        if (misalignment != 0)
        {
            Take("Alignment", alignment - misalignment);
        }
    }

    private bool Take(string name, int size)
    {
        if (Failed || size < 0 || Position < 0 || (long)Position + size > Buffer.Length || (long)Position + size > Limit)
        {
            Failed = true;
            return false;
        }

        if (Target != null && size > 0)
        {
            Target.AddField(name, size);
        }

        Position += size;
        return true;
    }

    public byte Byte(string name)
    {
        int position = Position;
        return Take(name, 1) ? Buffer.ReadByte(position) : (byte)0;
    }

    public ushort UInt16(string name)
    {
        int position = Position;
        return Take(name, 2) ? Buffer.ReadUInt16(position) : (ushort)0;
    }

    public int Int32(string name)
    {
        int position = Position;
        return Take(name, 4) ? Buffer.ReadInt32(position) : 0;
    }

    public uint UInt32(string name)
    {
        int position = Position;
        return Take(name, 4) ? Buffer.ReadUInt32(position) : 0;
    }

    public ulong UInt64(string name)
    {
        int position = Position;
        return Take(name, 8) ? Buffer.ReadUInt64(position) : 0;
    }

    public void Bytes(string name, int size)
    {
        Take(name, size);
    }

    public void Fail()
    {
        Failed = true;
    }

    /// <summary>
    /// An unsigned integer in the encoding of the compressed C++ exception
    /// data and of the .NET native format: the low bits of the first byte
    /// give the length (1 to 5 bytes).
    /// </summary>
    public uint CompressedUInt32(string name)
    {
        if (Failed || Position < 0 || Position >= Buffer.Length)
        {
            Failed = true;
            return 0;
        }

        int position = Position;
        byte first = Buffer.ReadByte(position);
        int length =
            (first & 1) == 0 ? 1 :
            (first & 2) == 0 ? 2 :
            (first & 4) == 0 ? 3 :
            (first & 8) == 0 ? 4 : 5;
        if (!Take(name, length))
        {
            return 0;
        }

        if (length == 5)
        {
            return Buffer.ReadUInt32(position + 1);
        }

        uint raw = 0;
        for (int i = 0; i < length; i++)
        {
            raw |= (uint)Buffer.ReadByte(position + i) << (8 * i);
        }

        return raw >> length;
    }
}
