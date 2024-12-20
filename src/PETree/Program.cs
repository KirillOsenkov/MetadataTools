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

        var peFile = new PEFileNode(buffer);
        peFile.Parse();
    }
}

public class ByteBuffer
{

}

public class StreamBuffer : ByteBuffer
{
    private FileStream stream;

    public StreamBuffer(FileStream stream)
    {
        this.stream = stream;
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
}

public class PEFileNode : Node
{
    private ByteBuffer buffer;

    public PEFileNode(ByteBuffer buffer)
    {
        this.buffer = buffer;
    }

    public override void Parse()
    {
        PEHeaderPointer = new FourBytes(buffer, 0x3C);
        Add(PEHeaderPointer);
    }

    public FourBytes PEHeaderPointer { get; set; }
}