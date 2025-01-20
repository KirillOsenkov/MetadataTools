using System.IO;

namespace GuiLabs.FileFormat;

public class ByteBuffer
{
    public virtual ushort ReadUInt16(int offset) => 0;
    public virtual short ReadInt16(int offset) => 0;
    public virtual uint ReadUInt32(int offset) => 0;
    public virtual ulong ReadUInt64(int offset) => 0;
    public virtual int ReadInt32(int offset) => 0;
    public virtual byte ReadByte(int offset) => 0;
    public virtual byte[] ReadBytes(int offset, int count) => null;
}

public class StreamBuffer : ByteBuffer
{
    private Stream stream;
    private BinaryReader binaryReader;

    public StreamBuffer(Stream stream)
    {
        this.stream = stream;
        this.binaryReader = new BinaryReader(stream);
    }

    public override uint ReadUInt32(int offset)
    {
        Position = offset;
        return binaryReader.ReadUInt32();
    }

    public override ulong ReadUInt64(int offset)
    {
        Position = offset;
        return binaryReader.ReadUInt64();
    }

    public override int ReadInt32(int offset)
    {
        Position = offset;
        return binaryReader.ReadInt32();
    }

    public override ushort ReadUInt16(int offset)
    {
        Position = offset;
        return binaryReader.ReadUInt16();
    }

    public override short ReadInt16(int offset)
    {
        Position = offset;
        return binaryReader.ReadInt16();
    }

    public override byte ReadByte(int offset)
    {
        Position = offset;
        return binaryReader.ReadByte();
    }

    public override byte[] ReadBytes(int offset, int count)
    {
        Position = offset;
        return binaryReader.ReadBytes(count);
    }

    public long Position
    {
        get => stream.Position;
        set
        {
            stream.Position = value;
        }
    }
}
