using System;
using System.Collections.Generic;
using System.IO;

namespace GuiLabs.PEFile;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        var filePath = args[0];
        if (!File.Exists(filePath))
        {
            return;
        }

        using var stream = new FileStream(filePath, FileMode.Open);

        var length = stream.Length;
        var buffer = new StreamBuffer(stream);

        var peFile = new PEFile(buffer);
        peFile.Parse();
    }
}

public class PEFile : Node
{
    public PEFile(ByteBuffer buffer) : base(buffer, 0)
    {
    }

    public override void Parse()
    {
        PEHeaderPointer = new FourBytes(Buffer, 0x3C);
        Add(PEHeaderPointer);

        int peHeaderPointer = PEHeaderPointer.ReadInt32();
        if (peHeaderPointer == 0)
        {
            peHeaderPointer = 0x80;
        }

        PEHeader = new PEHeader(Buffer, peHeaderPointer);
        Add(PEHeader);
    }

    public FourBytes PEHeaderPointer { get; set; }
    public PEHeader PEHeader { get; set; }
}

public class PEHeader : Node
{
    public PEHeader(ByteBuffer buffer, int start) : base(buffer, start)
    {
    }

    public override void Parse()
    {
        PEHeaderSignature = new FourBytes(Buffer, Start);
        Add(PEHeaderSignature);

        Platform = new TwoBytes(Buffer, Start + 4);
        Add(Platform);
    }

    public FourBytes PEHeaderSignature { get; set; }
    public TwoBytes Platform { get; set; }
}

public class ByteBuffer
{
    public virtual short ReadInt16(int offset) => 0;
    public virtual uint ReadUInt32(int offset) => 0;
    public virtual int ReadInt32(int offset) => 0;
}

public class StreamBuffer : ByteBuffer
{
    private FileStream stream;
    private BinaryReader binaryReader;

    public StreamBuffer(FileStream stream)
    {
        this.stream = stream;
        this.binaryReader = new BinaryReader(stream);
    }

    public override uint ReadUInt32(int offset)
    {
        Position = offset;
        return binaryReader.ReadUInt32();
    }

    public override int ReadInt32(int offset)
    {
        Position = offset;
        return binaryReader.ReadInt32();
    }

    public override short ReadInt16(int offset)
    {
        Position = offset;
        return binaryReader.ReadInt16();
    }

    private long position;
    public long Position
    {
        get => position;
        set
        {
            if (position == value)
            {
                return;
            }

            position = value;
            stream.Position = value;
        }
    }
}

public class Node
{
    public Node(ByteBuffer buffer, int start)
    {
        Buffer = buffer;
        Start = start;
    }

    public ByteBuffer Buffer { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }

    protected List<Node> children;
    protected List<Node> Children => children ??= [];

    public virtual void Parse()
    {
    }

    public virtual void Add(Node node)
    {
        Children.Add(node);
        node.Parse();
    }
}

public class TwoBytes : Node
{
    public TwoBytes(ByteBuffer buffer, int offset) : base(buffer, offset)
    {
        Length = 2;
    }

    public int ReadInt16() => Buffer.ReadInt16(Start);
}

public class FourBytes : Node
{
    public FourBytes(ByteBuffer buffer, int start) : base(buffer, start)
    {
        Length = 4;
    }

    public uint ReadUint32() => Buffer.ReadUInt32(Start);
    public int ReadInt32() => Buffer.ReadInt32(Start);
}
