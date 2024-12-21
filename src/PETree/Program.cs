using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

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

        var uncovered = new List<Span>();
        peFile.ComputeUncoveredSpans(s => uncovered.Add(s));
    }
}

public class PEFile : Node
{
    public PEFile(ByteBuffer buffer)
    {
        Buffer = buffer;
    }

    public override void Parse()
    {
        PEHeaderPointer = new FourBytes { Start = 0x3C };
        Add(PEHeaderPointer);

        int peHeaderPointer = PEHeaderPointer.Value;
        if (peHeaderPointer == 0)
        {
            peHeaderPointer = 0x80;
        }

        PEHeader = new PEHeader { Start = peHeaderPointer };
        Add(PEHeader);

        OptionalHeader = new OptionalHeader(PEHeader.SizeOfOptionalHeader.Value);
        Add(OptionalHeader);

        SectionTable = new SectionTable(PEHeader.NumberOfSections.Value);
        Add(SectionTable);

        int cliHeader = ResolveDataDirectory(OptionalHeader.DataDirectories.CLRRuntimeHeader);
        CLIHeader = new CLIHeader { Start = cliHeader };
        Add(CLIHeader);

        MetadataSection = GetSectionAtVirtualAddress(CLIHeader.Metadata.RVA.Value);
        int metadata = ResolveDataDirectory(CLIHeader.Metadata);
        Metadata = new Metadata { Start = metadata };
        Add(Metadata);

        var debugDirectoryAddress = OptionalHeader.DataDirectories.Debug;
        if (debugDirectoryAddress.RVA.Value != 0)
        {
            int offset = ResolveDataDirectory(debugDirectoryAddress);
            DebugDirectories = new DebugDirectories { Start = offset, Length = debugDirectoryAddress.Size.Value };
            Add(DebugDirectories);

            if (DebugDirectories.Directories.FirstOrDefault(d => d.DirectoryType == DebugDirectory.ImageDebugType.EmbeddedPortablePdb) is { } embeddedPdbDirectory)
            {
                var address = embeddedPdbDirectory.AddressOfRawData.Value;
                var start = ResolveVirtualAddress(address);
                if (start > 0)
                {
                    EmbeddedPdb = new EmbeddedPdb { Start = start, Length = embeddedPdbDirectory.SizeOfData.Value };
                    Add(EmbeddedPdb);
                }
            }
        }
    }

    public FourBytes PEHeaderPointer { get; set; }
    public PEHeader PEHeader { get; set; }
    public OptionalHeader OptionalHeader { get; set; }
    public SectionTable SectionTable { get; set; }
    public CLIHeader CLIHeader { get; set; }
    public Metadata Metadata { get; set; }
    public SectionHeader MetadataSection { get; set; }
    public DebugDirectories DebugDirectories { get; set; }
    public EmbeddedPdb EmbeddedPdb { get; set; }

    public int ResolveDataDirectory(DataDirectory dataDirectory)
    {
        return ResolveVirtualAddress(dataDirectory.RVA.Value);
    }

    public int ResolveVirtualAddress(int rva)
    {
        var section = GetSectionAtVirtualAddress(rva);
        if (section == null)
        {
            return 0;
        }

        return ResolveVirtualAddressInSection(rva, section);
    }

    public int ResolveMetadataOffset(int offset)
    {
        var result = MetadataSection.PointerToRawData.Value + offset;
        return result;
    }

    public int ResolveVirtualAddressInSection(int rva, SectionHeader section)
    {
        return (int)(rva + section.PointerToRawData.Value - section.VirtualAddress.Value);
    }

    public SectionHeader GetSectionAtVirtualAddress(int rva)
    {
        var sections = SectionTable.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var virtualAddress = section.VirtualAddress.Value;
            if (rva >= virtualAddress && rva < virtualAddress + section.SizeOfRawData.Value)
            {
                return section;
            }
        }

        return null;
    }
}

public class PEHeader : Node
{
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
    public OptionalHeader(short sizeOfOptionalHeader)
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
        Debug = Add<DataDirectory>();
        Architecture = AddEightBytes();
        GlobalPtr = AddEightBytes();
        TLSTable = AddEightBytes();
        LoadConfigTable = AddEightBytes();
        BoundImport = AddEightBytes();
        IAT = AddEightBytes();
        DelayImportDescriptor = AddEightBytes();
        CLRRuntimeHeader = Add<DataDirectory>();
        ReservedZero = AddEightBytes();
    }

    public EightBytes ExportTable { get; set; }
    public EightBytes ImportTable { get; set; }
    public EightBytes ResourceTable { get; set; }
    public EightBytes ExceptionTable { get; set; }
    public EightBytes CertificateTable { get; set; }
    public EightBytes BaseRelocationTable { get; set; }
    public DataDirectory Debug { get; set; }
    public EightBytes Architecture { get; set; }
    public EightBytes GlobalPtr { get; set; }
    public EightBytes TLSTable { get; set; }
    public EightBytes LoadConfigTable { get; set; }
    public EightBytes BoundImport { get; set; }
    public EightBytes IAT { get; set; }
    public EightBytes DelayImportDescriptor { get; set; }
    public DataDirectory CLRRuntimeHeader { get; set; }
    public EightBytes ReservedZero { get; set; }
}

public class SectionTable : Node
{
    public SectionTable(short count)
    {
        Count = count;
    }

    public short Count { get; }

    public override void Parse()
    {
        var list = new List<SectionHeader>();
        for (int i = 0; i < Count; i++)
        {
            list.Add(Add<SectionHeader>());
        }

        Sections = list;
    }

    public IReadOnlyList<SectionHeader> Sections { get; set; }
}

public class SectionHeader : Node
{
    public override void Parse()
    {
        Name = Add<EightByteString>();
        VirtualSize = AddFourBytes();
        VirtualAddress = AddFourBytes();
        SizeOfRawData = AddFourBytes();
        PointerToRawData = AddFourBytes();
        PointerToRelocations = AddFourBytes();
        PointerToLineNumbers = AddFourBytes();
        NumberOfRelocations = AddTwoBytes();
        NumberOfLineNumbers = AddTwoBytes();
        Characteristics = AddFourBytes();
    }

    public EightByteString Name { get; set; }
    public FourBytes VirtualSize { get; set; }
    public FourBytes VirtualAddress { get; set; }
    public FourBytes SizeOfRawData { get; set; }
    public FourBytes PointerToRawData { get; set; }
    public FourBytes PointerToRelocations { get; set; }
    public FourBytes PointerToLineNumbers { get; set; }
    public TwoBytes NumberOfRelocations { get; set; }
    public TwoBytes NumberOfLineNumbers { get; set; }
    public FourBytes Characteristics { get; set; }
}

public class CLIHeader : Node
{
    public override void Parse()
    {
        Cb = AddFourBytes();
        MajorRuntimeVersion = AddTwoBytes();
        MinorRuntimeVersion = AddTwoBytes();
        Metadata = Add<DataDirectory>();
        Flags = AddFourBytes();
        ImageAttributes = AddFourBytes();
        EntryPointToken = AddFourBytes();
        Resources = Add<DataDirectory>();
        StrongNameSignature = Add<DataDirectory>();
        CodeManagerTable = AddEightBytes();
        VTableFixups = AddEightBytes();
        ExportAddressTableJumps = AddEightBytes();
        ManagedNativeHeader = AddEightBytes();
    }

    public FourBytes Cb { get; set; }
    public TwoBytes MajorRuntimeVersion { get; set; }
    public TwoBytes MinorRuntimeVersion { get; set; }
    public DataDirectory Metadata { get; set; }
    public FourBytes Flags { get; set; }
    public FourBytes ImageAttributes { get; set; }
    public FourBytes EntryPointToken { get; set; }
    public DataDirectory Resources { get; set; }
    public DataDirectory StrongNameSignature { get; set; }
    public EightBytes CodeManagerTable { get; set; }
    public EightBytes VTableFixups { get; set; }
    public EightBytes ExportAddressTableJumps { get; set; }
    public EightBytes ManagedNativeHeader { get; set; }
}

public class Metadata : Node
{
    public override void Parse()
    {
        BSJB = AddFourBytes();
        MajorVersion = AddTwoBytes();
        MinorVersion = AddTwoBytes();
        Reserved = AddFourBytes();
        RuntimeVersion = Add<ZeroTerminatedStringLengthPrefix32>();
        Flags = AddTwoBytes();
        StreamCount = AddTwoBytes();

        var peFile = PEFile;

        int count = StreamCount.Value;
        var list = new MetadataStreamHeader[count];

        for (int i = 0; i < count; i++)
        {
            var stream = Add<MetadataStreamHeader>();
            list[i] = stream;

            int offset = stream.Offset.Value;
            int start = peFile != null ? peFile.ResolveMetadataOffset(offset) : offset;
            int length = stream.Size.Value;

            if (stream.Name.Text == "#~")
            {
                CompressedMetadataStream = new CompressedMetadataTableStream { Start = start, Length = length };
                if (peFile != null)
                {
                    peFile.Add(CompressedMetadataStream);
                }
                else
                {
                    Add(CompressedMetadataStream);
                }
            }
        }

        Streams = list;
    }

    public PEFile PEFile => FindAncestor<PEFile>();

    public FourBytes BSJB { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public FourBytes Reserved { get; set; }
    public TwoBytes Flags { get; set; }
    public TwoBytes StreamCount { get; set; }
    public ZeroTerminatedStringLengthPrefix32 RuntimeVersion { get; set; }
    public IReadOnlyList<MetadataStreamHeader> Streams { get; set; }

    public CompressedMetadataTableStream CompressedMetadataStream { get; set; }
}

public class MetadataStream : Node
{
}

public class CompressedMetadataTableStream : MetadataStream
{
    public override void Parse()
    {
        base.Parse();
    }
}

public class EmbeddedPdb : Node
{
    public override void Parse()
    {
        var length = Length;

        MPDB = AddFourBytes();
        DecompressedSize = AddFourBytes();
        CompressedStream = new CompressedDeflateStream
        {
            Length = length - 8,
            DecompressedSize = DecompressedSize.Value
        };
        Add(CompressedStream);
    }

    public FourBytes MPDB { get; set; }
    public FourBytes DecompressedSize { get; set; }
    public CompressedDeflateStream CompressedStream { get; set; }
}

public class CompressedDeflateStream : Node
{
    public override void Parse()
    {
        var bytes = Buffer.ReadBytes(Start, Length);
        var compressedMemoryStream = new MemoryStream(bytes);
        var deflateStream = new DeflateStream(compressedMemoryStream, CompressionMode.Decompress, leaveOpen: true);
        var decompressedStream = new MemoryStream(DecompressedSize);
        deflateStream.CopyTo(decompressedStream);

        var metadataBuffer = new StreamBuffer(decompressedStream);
        PdbMetadata = new Metadata
        {
            Buffer = metadataBuffer,
            Start = 0,
            Length = (int)decompressedStream.Length,
        };
        PdbMetadata.Parse();
    }

    public int DecompressedSize { get; set; }
    public Metadata PdbMetadata { get; set; }
}

public class MetadataStreamHeader : Node
{
    public override void Parse()
    {
        Offset = AddFourBytes();
        Size = AddFourBytes();
        Name = Add<ZeroTerminatedAlignedString>();
    }

    public FourBytes Offset { get; set; }
    public FourBytes Size { get; set; }
    public ZeroTerminatedString Name { get; set; }
}

public class DebugDirectories : Node
{
    public override void Parse()
    {
        int count = Length / 28;
        var list = new DebugDirectory[count];
        for (int i = 0; i < count; i++)
        {
            var directory = Add<DebugDirectory>();
            list[i] = directory;
        }

        Directories = list;
    }

    public IReadOnlyList<DebugDirectory> Directories { get; set; }
}

public class DebugDirectory : Node
{
    public override void Parse()
    {
        Characteristics = AddFourBytes();
        Timestamp = AddFourBytes();
        MajorVersion = AddTwoBytes();
        MinorVersion = AddTwoBytes();
        Type = AddFourBytes();
        SizeOfData = AddFourBytes();
        AddressOfRawData = AddFourBytes();
        PointerToRawData = AddFourBytes();

        DirectoryType = (ImageDebugType)Type.Value;
    }

    public FourBytes Characteristics { get; set; }
    public FourBytes Timestamp { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public FourBytes Type { get; set; }
    public FourBytes SizeOfData { get; set; }
    public FourBytes AddressOfRawData { get; set; }
    public FourBytes PointerToRawData { get; set; }

    public ImageDebugType DirectoryType { get; set; }

    public enum ImageDebugType : uint
    {
        Unknown = 0,
        Coff = 1,
        CodeView = 2,
        Fpo = 3,
        Misc = 4,
        Exception = 5,
        Fixup = 6,
        Borland = 9,
        Reproducible = 16,
        EmbeddedPortablePdb = 17,
        PdbChecksum = 19,
    }
}

public class DataDirectory : EightBytes
{
    public DataDirectory() : base()
    {
    }

    public override void Parse()
    {
        RVA = AddFourBytes();
        Size = AddFourBytes();
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
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

public class Node
{
    public Node()
    {
    }

    public Node(ByteBuffer buffer, int start)
    {
        Buffer = buffer;
        Start = start;
    }

    public ByteBuffer Buffer { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
    public int End => Start + Length;

    public Node Parent { get; set; }

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
    public List<Node> Children => children ??= [];

    public bool HasChildren => children != null && children.Count > 0;

    public virtual void Parse()
    {
    }

    public virtual void Add(Node node)
    {
        bool inserted = false;
        if (node.Start != 0)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                var child = Children[i];
                if (child.Start > node.Start)
                {
                    Children.Insert(i, node);
                    inserted = true;
                    break;
                }
            }
        }

        if (!inserted)
        {
            if (node.Start == 0)
            {
                // This needs to run before we add this node to Children,
                // so we can access the previous child
                node.Start = LastChildEnd;
            }

            Children.Add(node);
        }

        node.Buffer = Buffer;
        node.Parent = this;

        node.Parse();
        Length = LastChildEnd - Start;
    }

    public T FindAncestor<T>() where T : Node => Parent == null ? null : Parent is T t ? t : Parent.FindAncestor<T>();

    public OneByte AddOneByte() => Add<OneByte>();
    public TwoBytes AddTwoBytes() => Add<TwoBytes>();
    public FourBytes AddFourBytes() => Add<FourBytes>();
    public EightBytes AddEightBytes() => Add<EightBytes>();

    public byte[] ReadBytes(int offset, int length) => Buffer.ReadBytes(offset, length);

    public T Add<T>() where T : Node, new()
    {
        int start = LastChildEnd;
        var result = new T();
        result.Buffer = Buffer;
        result.Start = start;
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
    public BytesNode()
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
    public OneByte()
    {
        Length = 1;
    }

    public byte ReadByte() => Buffer.ReadByte(Start);
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
    public ulong ReadUInt64() => Buffer.ReadUInt32(Start);
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
                Zero = new OneByte() { Start = offset - 1 };
                Add(Zero);
                offset = Align(chars.Count, offset);
                Length = offset - Start;
                if (requiredLength > Length)
                {
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
    public OneByte Zero { get; set; }
}

public class ZeroTerminatedAlignedString : ZeroTerminatedString
{
    protected override int Align(int read, int position)
    {
        position += -1 + ((read + 4) & ~3) - read;
        return position;
    }
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

    public static void ComputeUncoveredSpans(this Node node, Action<Span> collector)
    {
        if (!node.HasChildren)
        {
            return;
        }

        int index = node.Start;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (child.Start > index)
            {
                collector(new Span(index, child.Start - index));
            }

            ComputeUncoveredSpans(child, collector);

            index = child.End;
        }

        if (index < node.End)
        {
            collector(new Span(index, node.End - index));
        }
    }
}

public record struct Span(int Start, int Length);