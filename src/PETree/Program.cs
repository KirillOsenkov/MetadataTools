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

public class PEFile(ByteBuffer buffer) : Node
{
    public override void Parse()
    {
        PEHeaderPointer = new FourBytes(buffer, 0x3C);
        Add(PEHeaderPointer);

        int peHeaderPointer = PEHeaderPointer.ReadInt32();
        if (peHeaderPointer == 0)
        {
            peHeaderPointer = 0x80;
        }

        PEHeader = new PEHeader(buffer, peHeaderPointer);
        Add(PEHeader);
    }

    public FourBytes PEHeaderPointer { get; set; }
    public PEHeader PEHeader { get; set; }
}

public class PEHeader(ByteBuffer buffer, int offset) : Node
{
    public override void Parse()
    {
        PEHeaderSignature = new FourBytes(buffer, offset);
        Add(PEHeaderSignature);


    }

    public FourBytes PEHeaderSignature { get; set; }
}

public class ByteBuffer
{
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
    protected List<Node> children;
    protected List<Node> Children
    {
        get
        {
            if (children == null)
            {
                children = new();
            }

            return children;
        }
    }

    public virtual void Parse()
    {
    }

    public virtual void Add(Node node)
    {
        Children.Add(node);
        node.Parse();
    }
}

public class FourBytes : Node
{
    private ByteBuffer buffer;
    private readonly int offset;

    public FourBytes(ByteBuffer buffer, int offset)
    {
        this.buffer = buffer;
        this.offset = offset;
    }

    public uint ReadUint32()
    {
        return buffer.ReadUInt32(offset);
    }

    public int ReadInt32()
    {
        return buffer.ReadInt32(offset);
    }
}

