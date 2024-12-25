using System.Collections.Generic;
using GuiLabs.PEFile;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat;

public class Sequence : Node
{
}

public class BytesNode : Node
{
    public BytesNode()
    {
    }

    public override string ToString()
    {
        var bytes = Buffer.ReadBytes(Start, Length);
        return $"{bytes.ToHexString()}";
    }
}

public class Padding : Node
{
}

public class OneByte : BytesNode
{
    public OneByte()
    {
        Length = 1;
    }

    public byte ReadByte() => Buffer.ReadByte(Start);

    public byte Value => ReadByte();
}

public class TwoBytes : BytesNode
{
    public TwoBytes()
    {
        Length = 2;
    }

    public short ReadInt16() => Buffer.ReadInt16(Start);

    public short Value => ReadInt16();
}

public class FourBytes : BytesNode
{
    public FourBytes()
    {
        Length = 4;
    }

    public uint ReadUint32() => Buffer.ReadUInt32(Start);
    public int ReadInt32() => Buffer.ReadInt32(Start);

    public int Value => ReadInt32();
}

public class EightBytes : BytesNode
{
    public EightBytes()
    {
        Length = 8;
    }

    public uint ReadUint32() => Buffer.ReadUInt32(Start);
    public int ReadInt32() => Buffer.ReadInt32(Start);
    public ulong ReadUInt64() => Buffer.ReadUInt64(Start);
}

public class EightByteString : EightBytes
{
    public override void Parse()
    {
        Text = ReadBytes(Start, 8).ReadZeroTerminatedString();
    }

    public string Text { get; set; }
}

public class ZeroTerminatedStringLengthPrefix32 : Node
{
    public override void Parse()
    {
        Length32 = AddFourBytes();
        ZeroTerminatedString = new ZeroTerminatedString { Length = Length32.Value };
        Add(ZeroTerminatedString);
    }

    public FourBytes Length32 { get; set; }
    public ZeroTerminatedString ZeroTerminatedString { get; set; }
}

public class ZeroTerminatedString : Node
{
    public override void Parse()
    {
        List<char> chars = new();

        int requiredLength = Length;

        int offset = Start;
        while (true)
        {
            byte b = Buffer.ReadByte(offset);
            offset++;
            if (b == 0)
            {
                String = new Utf8String { Start = Start, Length = chars.Count };
                Add(String);

                Zero = new OneByte() { Start = offset - 1 };
                Add(Zero);
                int aligned = Align(chars.Count, offset);
                if (aligned > offset)
                {
                    PaddingZeroes = new Node { Start = offset, Length = aligned - offset };
                    Add(PaddingZeroes);
                }

                offset = aligned;
                Length = offset - Start;
                if (requiredLength > Length && PaddingZeroes == null)
                {
                    PaddingZeroes = new Node { Start = offset, Length = requiredLength - Length };
                    Add(PaddingZeroes);
                    Length = requiredLength;
                }

                break;
            }

            chars.Add((char)b);
        }

        Text = new string(chars.ToArray());
    }

    protected virtual int Align(int length, int position)
    {
        return position;
    }

    public string Text { get; set; }
    public Utf8String String { get; set; }
    public OneByte Zero { get; set; }
    public Node PaddingZeroes { get; set; }
}

public class Utf8String : Node
{
}

public class ZeroTerminatedAlignedString : ZeroTerminatedString
{
    protected override int Align(int read, int position)
    {
        position += -1 + ((read + 4) & ~3) - read;
        return position;
    }
}
