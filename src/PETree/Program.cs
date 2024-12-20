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

        OptionalHeader = new OptionalHeader(Buffer, peHeaderPointer + 80, PEHeader.SizeOfOptionalHeader.ReadInt16());
        Add(OptionalHeader);
    }

    public FourBytes PEHeaderPointer { get; set; }
    public PEHeader PEHeader { get; set; }
    public OptionalHeader OptionalHeader { get; set; }
}

public class PEHeader : Node
{
    public PEHeader(ByteBuffer buffer, int start) : base(buffer, start)
    {
    }

    public override void Parse()
    {
        PEHeaderSignature = AddFourBytes();

        Platform = AddTwoBytes();
        NumberOfSections = AddTwoBytes();
        TimeDateStamp = AddFourBytes();
        PointerToSymbolTable = AddFourBytes();
        NumberOfSymbols = AddFourBytes();
        SizeOfOptionalHeader = AddTwoBytes();
        Characteristics = AddTwoBytes();
    }

    public FourBytes PEHeaderSignature { get; set; }
    public TwoBytes Platform { get; set; }
    public TwoBytes NumberOfSections { get; set; }
    public FourBytes TimeDateStamp { get; set; }
    public FourBytes PointerToSymbolTable { get; set; }
    public FourBytes NumberOfSymbols { get; set; }
    public TwoBytes SizeOfOptionalHeader { get; set; }
    public TwoBytes Characteristics { get; set; }
}

public class OptionalHeader : Node
{
    public OptionalHeader(ByteBuffer buffer, int start, short sizeOfOptionalHeader) : base(buffer, start)
    {
        SizeOfOptionalHeader = sizeOfOptionalHeader;
    }

    public override void Parse()
    {
        StandardFields = new OptionalHeaderStandardFields(Buffer, Start);
        Add(StandardFields);

        var isPE32Plus = StandardFields.IsPE32Plus;

        WindowsFields = new OptionalHeaderWindowsFields(Buffer, StandardFields.End, isPE32Plus);
        Add(WindowsFields);

        DataDirectories = new OptionalHeaderDataDirectories(Buffer, WindowsFields.End, isPE32Plus);
        Add(DataDirectories);
    }

    public short SizeOfOptionalHeader { get; }
    public OptionalHeaderStandardFields StandardFields { get; set; }
    public OptionalHeaderWindowsFields WindowsFields { get; set; }
    public OptionalHeaderDataDirectories DataDirectories { get; set; }
}

public class OptionalHeaderStandardFields : Node
{
    public OptionalHeaderStandardFields(ByteBuffer buffer, int start) : base(buffer, start)
    {
    }

    public override void Parse()
    {
        Magic = AddTwoBytes();
        IsPE32Plus = Magic.ReadInt16() == 0x20B;

        MajorLinkerVersion = AddOneByte();
        MinorLinkerVersion = AddOneByte();
        SizeOfCode = AddFourBytes();
        SizeOfInitializedData = AddFourBytes();
        SizeOfUninitializedData = AddFourBytes();
        AddressOfEntryPoint = AddFourBytes();
        BaseOfCode = AddFourBytes();

        if (!IsPE32Plus)
        {
            BaseOfData = AddFourBytes();
        }
    }

    public TwoBytes Magic { get; set; }
    public bool IsPE32Plus { get; set; }
    public OneByte MajorLinkerVersion { get; set; }
    public OneByte MinorLinkerVersion { get; set; }
    public FourBytes SizeOfCode { get; set; }
    public FourBytes SizeOfInitializedData { get; set; }
    public FourBytes SizeOfUninitializedData { get; set; }
    public FourBytes AddressOfEntryPoint { get; set; }
    public FourBytes BaseOfCode { get; set; }
    public FourBytes BaseOfData { get; set; }
}

public class OptionalHeaderWindowsFields : Node
{
    public OptionalHeaderWindowsFields(ByteBuffer buffer, int start, bool isPE32Plus) : base(buffer, start)
    {
        IsPE32Plus = isPE32Plus;
    }

    public bool IsPE32Plus { get; }

    public override void Parse()
    {
        ImageBase = AddFourOrEightBytes(IsPE32Plus);
        SectionAlignment = AddFourBytes();
        FileAlignment = AddFourBytes();
        MajorOperationSystemVersion = AddTwoBytes();
        MinorOperatingSystemVersion = AddTwoBytes();
        MajorImageVersion = AddTwoBytes();
        MinorImageVersion = AddTwoBytes();
        MajorSubsystemVersion = AddTwoBytes();
        MinorSubsystemVersion = AddTwoBytes();
        Win32VersionValue = AddFourBytes();
        SizeOfImage = AddFourBytes();
        SizeOfHeaders = AddFourBytes();
        Checksum = AddFourBytes();
        Subsystem = AddTwoBytes();
        DllCharacteristics = AddTwoBytes();
        SizeOfStackReserve = AddFourOrEightBytes(IsPE32Plus);
        SizeOfStackCommit = AddFourOrEightBytes(IsPE32Plus);
        SizeOfHeapReserve = AddFourOrEightBytes(IsPE32Plus);
        SizeOfHeapCommit = AddFourOrEightBytes(IsPE32Plus);
        LoaderFlags = AddFourBytes();
        NumberOfRvaAndSizes = AddFourBytes();
    }

    public BytesNode ImageBase { get; set; }
    public FourBytes SectionAlignment { get; set; }
    public FourBytes FileAlignment { get; set; }
    public TwoBytes MajorOperationSystemVersion { get; set; }
    public TwoBytes MinorOperatingSystemVersion { get; set; }
    public TwoBytes MajorImageVersion { get; set; }
    public TwoBytes MinorImageVersion { get; set; }
    public TwoBytes MajorSubsystemVersion { get; set; }
    public TwoBytes MinorSubsystemVersion { get; set; }
    public FourBytes Win32VersionValue { get; set; }
    public FourBytes SizeOfImage { get; set; }
    public FourBytes SizeOfHeaders { get; set; }
    public FourBytes Checksum { get; set; }
    public TwoBytes Subsystem { get; set; }
    public TwoBytes DllCharacteristics { get; set; }
    public BytesNode SizeOfStackReserve { get; set; }
    public BytesNode SizeOfStackCommit { get; set; }
    public BytesNode SizeOfHeapReserve { get; set; }
    public BytesNode SizeOfHeapCommit { get; set; }
    public FourBytes LoaderFlags { get; set; }
    public FourBytes NumberOfRvaAndSizes { get; set; }
}

public class OptionalHeaderDataDirectories : Node
{
    public OptionalHeaderDataDirectories(ByteBuffer buffer, int start, bool isPE32Plus) : base(buffer, start)
    {
    }

    public override void Parse()
    {
        ExportTable = AddEightBytes();
        ImportTable = AddEightBytes();
        ResourceTable = AddEightBytes();
        ExceptionTable = AddEightBytes();
        CertificateTable = AddEightBytes();
        BaseRelocationTable = AddEightBytes();
        Debug = AddEightBytes();
        Architecture = AddEightBytes();
        GlobalPtr = AddEightBytes();
        TLSTable = AddEightBytes();
        LoadConfigTable = AddEightBytes();
        BoundImport = AddEightBytes();
        IAT = AddEightBytes();
        DelayImportDescriptor = AddEightBytes();
        CLRRuntimeHeader = AddEightBytes();
        ReservedZero = AddEightBytes();
    }

    public EightBytes ExportTable { get; set; }
    public EightBytes ImportTable { get; set; }
    public EightBytes ResourceTable { get; set; }
    public EightBytes ExceptionTable { get; set; }
    public EightBytes CertificateTable { get; set; }
    public EightBytes BaseRelocationTable { get; set; }
    public EightBytes Debug { get; set; }
    public EightBytes Architecture { get; set; }
    public EightBytes GlobalPtr { get; set; }
    public EightBytes TLSTable { get; set; }
    public EightBytes LoadConfigTable { get; set; }
    public EightBytes BoundImport { get; set; }
    public EightBytes IAT { get; set; }
    public EightBytes DelayImportDescriptor { get; set; }
    public EightBytes CLRRuntimeHeader { get; set; }
    public EightBytes ReservedZero { get; set; }
}

public class ByteBuffer
{
    public virtual short ReadInt16(int offset) => 0;
    public virtual uint ReadUInt32(int offset) => 0;
    public virtual ulong ReadUInt64(int offset) => 0;
    public virtual int ReadInt32(int offset) => 0;
    public virtual byte ReadByte(int offset) => 0;
    public virtual byte[] ReadBytes(int offset, int count) => null;
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
    public int End => Start + Length;

    public int LastChildEnd
    {
        get
        {
            int end = Start;

            if (HasChildren)
            {
                end = Children[Children.Count - 1].End;
            }

            return end;
        }
    }

    protected List<Node> children;
    protected List<Node> Children => children ??= [];

    public bool HasChildren => children != null && children.Count > 0;

    public virtual void Parse()
    {
    }

    public virtual void Add(Node node)
    {
        Children.Add(node);
        node.Parse();
        Length = LastChildEnd - Start;
    }

    public OneByte AddOneByte()
    {
        int start = LastChildEnd;
        var result = new OneByte(Buffer, start);
        Add(result);
        return result;
    }

    public TwoBytes AddTwoBytes()
    {
        int start = LastChildEnd;
        var result = new TwoBytes(Buffer, start);
        Add(result);
        return result;
    }

    public FourBytes AddFourBytes()
    {
        int start = LastChildEnd;
        var result = new FourBytes(Buffer, start);
        Add(result);
        return result;
    }

    public EightBytes AddEightBytes()
    {
        int start = LastChildEnd;
        var result = new EightBytes(Buffer, start);
        Add(result);
        return result;
    }

    public BytesNode AddFourOrEightBytes(bool eight)
    {
        if (eight)
        {
            return AddEightBytes();
        }
        else
        {
            return AddFourBytes();
        }
    }

    public override string ToString()
    {
        if (Length <= 32)
        {
            var bytes = Buffer.ReadBytes(Start, Length);
            return $"{GetType().Name} {bytes.ToHexString()}";
        }

        return $"{GetType().Name} {Start:x0} ({Length} bytes)";
    }
}

public class BytesNode : Node
{
    public BytesNode(ByteBuffer buffer, int start) : base(buffer, start)
    {
    }

    public override string ToString()
    {
        var bytes = Buffer.ReadBytes(Start, Length);
        return $"{bytes.ToHexString()}";
    }
}

public class OneByte : BytesNode
{
    public OneByte(ByteBuffer buffer, int offset) : base(buffer, offset)
    {
        Length = 1;
    }

    public byte ReadByte() => Buffer.ReadByte(Start);
}

public class TwoBytes : BytesNode
{
    public TwoBytes(ByteBuffer buffer, int offset) : base(buffer, offset)
    {
        Length = 2;
    }

    public short ReadInt16() => Buffer.ReadInt16(Start);
}

public class FourBytes : BytesNode
{
    public FourBytes(ByteBuffer buffer, int start) : base(buffer, start)
    {
        Length = 4;
    }

    public uint ReadUint32() => Buffer.ReadUInt32(Start);
    public int ReadInt32() => Buffer.ReadInt32(Start);
}

public class EightBytes : BytesNode
{
    public EightBytes(ByteBuffer buffer, int start) : base(buffer, start)
    {
        Length = 8;
    }

    public uint ReadUint32() => Buffer.ReadUInt32(Start);
    public int ReadInt32() => Buffer.ReadInt32(Start);
    public ulong ReadUInt64() => Buffer.ReadUInt32(Start);
}

internal static class Extensions
{
    public static string ToHexString(this byte[] bytes, char separator = ' ')
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        const int multiplier = 3;
        int digits = bytes.Length * multiplier;

        char[] c = new char[digits];
        byte b;
        for (int i = 0; i < digits / multiplier; i++)
        {
            b = ((byte)(bytes[i] >> 4));
            c[i * multiplier] = (char)(b > 9 ? b + 55 : b + 0x30);
            b = ((byte)(bytes[i] & 0xF));
            c[i * multiplier + 1] = (char)(b > 9 ? b + 55 : b + 0x30);
            c[i * 3 + 2] = separator;
        }

        return new string(c);
    }
}