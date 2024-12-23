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
        peFile.Length = (int)length;

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
        DOSHeaderAndStub = new Node();
        Add(DOSHeaderAndStub);

        var DOSHeader = new Node { Length = 0x3C };
        DOSHeaderAndStub.Add(DOSHeader);

        PEHeaderPointer = DOSHeaderAndStub.AddFourBytes();

        var DOSStub = new Node { Length = 0x40 };
        DOSHeaderAndStub.Add(DOSStub);

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

        TextSection = AddSection(".text");
        RsrcSection = AddSection(".rsrc");
        RelocSection = AddSection(".reloc");

        int cliHeader = ResolveDataDirectory(OptionalHeader.DataDirectories.CLRRuntimeHeader);
        CLIHeader = new CLIHeader { Start = cliHeader };
        TextSection.Add(CLIHeader);

        var metadataDirectory = CLIHeader.Metadata;
        MetadataRVA = metadataDirectory.RVA.Value;
        MetadataSectionHeader = GetSectionAtVirtualAddress(MetadataRVA);
        int metadata = ResolveDataDirectory(metadataDirectory);
        Metadata = new Metadata { Start = metadata };
        TextSection.Add(Metadata);

        var debugDirectoryAddress = OptionalHeader.DataDirectories.Debug;
        if (debugDirectoryAddress.RVA.Value != 0)
        {
            int offset = ResolveDataDirectory(debugDirectoryAddress);
            DebugDirectories = new DebugDirectories { Start = offset, Length = debugDirectoryAddress.Size.Value };
            TextSection.Add(DebugDirectories);

            if (DebugDirectories.Directories.FirstOrDefault(d => d.DirectoryType == DebugDirectory.ImageDebugType.EmbeddedPortablePdb) is { } embeddedPdbDirectory)
            {
                var address = embeddedPdbDirectory.AddressOfRawData.Value;
                var start = ResolveVirtualAddress(address);
                if (start > 0)
                {
                    EmbeddedPdb = new EmbeddedPdb { Start = start, Length = embeddedPdbDirectory.SizeOfData.Value };
                    TextSection.Add(EmbeddedPdb);
                }
            }
        }

        ResourceTable = AddTable(OptionalHeader.DataDirectories.ResourceTable);

        AddTable(OptionalHeader.DataDirectories.BaseRelocationTable);
        AddTable(OptionalHeader.DataDirectories.BoundImport);
        AddTable(OptionalHeader.DataDirectories.CertificateTable, isRVA: false);
        AddTable(OptionalHeader.DataDirectories.ExceptionTable);
        AddTable(OptionalHeader.DataDirectories.ExportTable);
        AddTable(OptionalHeader.DataDirectories.ImportTable);
        AddTable(OptionalHeader.DataDirectories.LoadConfigTable);
        AddTable(OptionalHeader.DataDirectories.TLSTable);
        AddTable(OptionalHeader.DataDirectories.IAT);
    }

    private Node AddTable(DataDirectory dataDirectory, bool isRVA = true)
    {
        if (dataDirectory.Size.Value > 0)
        {
            var offset = dataDirectory.RVA.Value;
            var resolved = isRVA ? ResolveVirtualAddress(offset) : offset;
            if (resolved == 0)
            {
                resolved = offset;
            }

            var node = new Node
            {
                Start = resolved,
                Length = dataDirectory.Size.Value
            };
            Add(node);
            return node;
        }

        return null;
    }

    private Section AddSection(string name)
    {
        var sectionHeader = SectionTable.SectionHeaders.FirstOrDefault(s => s.Name.Text == name);
        if (sectionHeader != null)
        {
            var section = new Section
            {
                Start = sectionHeader.PointerToRawData.Value,
                Length = sectionHeader.SizeOfRawData.Value,
                Name = name
            };
            Add(section);
            return section;
        }

        return null;
    }

    public Node DOSHeaderAndStub { get; set; }
    public FourBytes PEHeaderPointer { get; set; }
    public PEHeader PEHeader { get; set; }
    public OptionalHeader OptionalHeader { get; set; }
    public SectionTable SectionTable { get; set; }
    public CLIHeader CLIHeader { get; set; }
    public Metadata Metadata { get; set; }
    public SectionHeader MetadataSectionHeader { get; set; }
    public int MetadataRVA { get; set; }
    public DebugDirectories DebugDirectories { get; set; }
    public EmbeddedPdb EmbeddedPdb { get; set; }
    public Node ResourceTable { get; set; }
    public Node TextSection { get; set; }
    public Node RsrcSection { get; set; }
    public Node RelocSection { get; set; }

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
        var result = MetadataRVA - MetadataSectionHeader.VirtualAddress.Value + offset;
        result = result + MetadataSectionHeader.PointerToRawData.Value;
        return result;
    }

    public int ResolveVirtualAddressInSection(int rva, SectionHeader section)
    {
        return (int)(rva + section.PointerToRawData.Value - section.VirtualAddress.Value);
    }

    public SectionHeader GetSectionAtVirtualAddress(int rva)
    {
        var sections = SectionTable.SectionHeaders;
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
        ExportTable = Add<DataDirectory>();
        ImportTable = Add<DataDirectory>();
        ResourceTable = Add<DataDirectory>();
        ExceptionTable = Add<DataDirectory>();
        CertificateTable = Add<DataDirectory>();
        BaseRelocationTable = Add<DataDirectory>();
        Debug = Add<DataDirectory>();
        Architecture = Add<DataDirectory>();
        GlobalPtr = Add<DataDirectory>();
        TLSTable = Add<DataDirectory>();
        LoadConfigTable = Add<DataDirectory>();
        BoundImport = Add<DataDirectory>();
        IAT = Add<DataDirectory>();
        DelayImportDescriptor = Add<DataDirectory>();
        CLRRuntimeHeader = Add<DataDirectory>();
        ReservedZero = Add<DataDirectory>();
    }

    public DataDirectory ExportTable { get; set; }
    public DataDirectory ImportTable { get; set; }
    public DataDirectory ResourceTable { get; set; }
    public DataDirectory ExceptionTable { get; set; }
    public DataDirectory CertificateTable { get; set; }
    public DataDirectory BaseRelocationTable { get; set; }
    public DataDirectory Debug { get; set; }
    public DataDirectory Architecture { get; set; }
    public DataDirectory GlobalPtr { get; set; }
    public DataDirectory TLSTable { get; set; }
    public DataDirectory LoadConfigTable { get; set; }
    public DataDirectory BoundImport { get; set; }
    public DataDirectory IAT { get; set; }
    public DataDirectory DelayImportDescriptor { get; set; }
    public DataDirectory CLRRuntimeHeader { get; set; }
    public DataDirectory ReservedZero { get; set; }
}

public class Section : Node
{
    public string Name { get; set; }
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

        SectionHeaders = list;
    }

    public IReadOnlyList<SectionHeader> SectionHeaders { get; set; }
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

        var embeddedPdbStreams = new List<MetadataStream>();

        for (int i = 0; i < count; i++)
        {
            var stream = Add<MetadataStreamHeader>();
            list[i] = stream;

            int offset = stream.Offset.Value;
            int start = peFile != null ? peFile.ResolveMetadataOffset(offset) : offset;
            int length = stream.Size.Value;
            string streamName = stream.Name.Text;

            MetadataStream metadataStream = null;

            if (streamName == "#~")
            {
                metadataStream = CompressedMetadataTableStream = new CompressedMetadataTableStream();
            }
            else if (streamName == "#-")
            {
                metadataStream = UncompressedMetadataTableStream = new UncompressedMetadataTableStream();
            }
            else if (streamName == "#Strings")
            {
                metadataStream = StringsTableStream = new MetadataStream();
            }
            else if (streamName == "#Blob")
            {
                metadataStream = BlobTableStream = new MetadataStream();
            }
            else if (streamName == "#GUID")
            {
                metadataStream = GuidTableStream = new MetadataStream();
            }
            else if (streamName == "#US")
            {
                metadataStream = UserStringsTableStream = new MetadataStream();
            }
            else if (streamName == "#JTD" || streamName == "#Pdb")
            {
                metadataStream = new MetadataStream();
            }

            if (metadataStream != null)
            {
                metadataStream.Name = streamName;
                metadataStream.Start = start;
                metadataStream.Length = length;
                if (peFile != null)
                {
                    peFile.TextSection.Add(metadataStream);
                }
                else
                {
                    embeddedPdbStreams.Add(metadataStream);
                }
            }
        }

        for (int i = 0; i < embeddedPdbStreams.Count; i++)
        {
            Add(embeddedPdbStreams[i]);
        }

        StreamHeaders = list;

        int heapsizes = CompressedMetadataTableStream.HeapSizes.Value;
        if (StringsTableStream != null)
        {
            StringsTableStream.IndexSize = (heapsizes & 1) == 1 ? 4 : 2;
        }

        if (GuidTableStream != null)
        {
            GuidTableStream.IndexSize = (heapsizes & 2) == 1 ? 4 : 2;
        }

        if (BlobTableStream != null)
        {
            BlobTableStream.IndexSize = (heapsizes & 4) == 1 ? 4 : 2;
        }
    }

    public PEFile PEFile => FindAncestor<PEFile>();

    public FourBytes BSJB { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public FourBytes Reserved { get; set; }
    public TwoBytes Flags { get; set; }
    public TwoBytes StreamCount { get; set; }
    public ZeroTerminatedStringLengthPrefix32 RuntimeVersion { get; set; }
    public IReadOnlyList<MetadataStreamHeader> StreamHeaders { get; set; }

    public CompressedMetadataTableStream CompressedMetadataTableStream { get; set; }
    public UncompressedMetadataTableStream UncompressedMetadataTableStream { get; set; }
    public MetadataStream StringsTableStream { get; set; }
    public MetadataStream GuidTableStream { get; set; }
    public MetadataStream BlobTableStream { get; set; }
    public MetadataStream UserStringsTableStream { get; set; }
    public EmbeddedPdb EmbeddedPdb { get; set; }
}

public class MetadataStream : Node
{
    public int IndexSize { get; set; }
    public string Name { get; set; }
}

public struct TableInfo
{
    public int RowSize;
    public int RowCount;
}

public class CompressedMetadataTableStream : MetadataStream
{
    public const int MaxTables = 58;

    public TableInfo[] TableInfos = new TableInfo[MaxTables];

    public override void Parse()
    {
        ReservedZero = AddFourBytes();
        MajorVersion = AddOneByte();
        MinorVersion = AddOneByte();
        HeapSizes = AddOneByte();
        ReservedByte = AddOneByte();
        Valid = AddEightBytes();
        Sorted = AddEightBytes();

        TableLengths = Add<Sequence>();

        ulong valid = Valid.ReadUInt64();

        for (int i = 0; i < MaxTables; i++)
        {
            if ((valid & (1UL << i)) == 0)
            {
                continue;
            }

            var tableLength = TableLengths.AddFourBytes();

            TableInfos[i].RowCount = tableLength.Value;
        }

        PEFile = FindAncestor<PEFile>();

        ComputeTableInformations();
    }

    public FourBytes ReservedZero { get; set; }
    public OneByte MajorVersion { get; set; }
    public OneByte MinorVersion { get; set; }
    public OneByte HeapSizes { get; set; }
    public OneByte ReservedByte { get; set; }
    public EightBytes Valid { get; set; }
    public EightBytes Sorted { get; set; }
    public Sequence TableLengths { get; set; }

    public IReadOnlyList<MetadataTable> Tables { get; set; }

    public Metadata Metadata => Parent switch
    {
        Metadata m => m,
        PEFile peFile => peFile.Metadata,
        Node textSection => ((PEFile)textSection.Parent).Metadata,
        _ => null
    };

    public PEFile PEFile { get; set; }

    int GetTableIndexSize(Table table) => TableInfos[(int)table].RowCount < 65536 ? 2 : 4;

    readonly int[] coded_index_sizes = new int[14];

    int GetCodedIndexSize(CodedIndex codedIndex)
    {
        var index = (int)codedIndex;
        var size = coded_index_sizes[index];
        if (size != 0)
        {
            return size;
        }

        return coded_index_sizes[index] = codedIndex.GetSize(t => TableInfos[(int)t].RowCount);
    }

    void ComputeTableInformations()
    {
        int heapsizes = HeapSizes.Value;
        int stridx_size = 2;
        int guididx_size = 2;
        int blobidx_size = 2;

        if (Metadata.StringsTableStream != null && (heapsizes & 1) == 1)
        {
            stridx_size = 4;
        }

        if (Metadata.GuidTableStream != null && (heapsizes & 2) == 1)
        {
            guididx_size = 4;
        }

        if (Metadata.BlobTableStream != null && (heapsizes & 4) == 1)
        {
            blobidx_size = 4;
        }

        ulong valid = Valid.ReadUInt64();

        var tables = new List<MetadataTable>();

        for (int i = 0; i < MaxTables; i++)
        {
            if ((valid & (1UL << i)) == 0)
            {
                continue;
            }

            int size;
            Table tableKind = (Table)i;
            switch (tableKind)
            {
                case Table.Module:
                    size = 2    // Generation
                        + stridx_size   // Name
                        + (guididx_size * 3);   // Mvid, EncId, EncBaseId
                    break;
                case Table.TypeRef:
                    size = GetCodedIndexSize(CodedIndex.ResolutionScope)    // ResolutionScope
                        + (stridx_size * 2);    // Name, Namespace
                    break;
                case Table.TypeDef:
                    size = 4    // Flags
                        + (stridx_size * 2) // Name, Namespace
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef)    // BaseType
                        + GetTableIndexSize(Table.Field)    // FieldList
                        + GetTableIndexSize(Table.Method);  // MethodList
                    break;
                case Table.FieldPtr:
                    size = GetTableIndexSize(Table.Field);  // Field
                    break;
                case Table.Field:
                    size = 2    // Flags
                        + stridx_size   // Name
                        + blobidx_size; // Signature
                    break;
                case Table.MethodPtr:
                    size = GetTableIndexSize(Table.Method); // Method
                    break;
                case Table.Method:
                    size = 8    // Rva 4, ImplFlags 2, Flags 2
                        + stridx_size   // Name
                        + blobidx_size  // Signature
                        + GetTableIndexSize(Table.Param); // ParamList
                    break;
                case Table.ParamPtr:
                    size = GetTableIndexSize(Table.Param); // Param
                    break;
                case Table.Param:
                    size = 4    // Flags 2, Sequence 2
                        + stridx_size;  // Name
                    break;
                case Table.InterfaceImpl:
                    size = GetTableIndexSize(Table.TypeDef) // Class
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef);   // Interface
                    break;
                case Table.MemberRef:
                    size = GetCodedIndexSize(CodedIndex.MemberRefParent)    // Class
                        + stridx_size   // Name
                        + blobidx_size; // Signature
                    break;
                case Table.Constant:
                    size = 2    // Type
                        + GetCodedIndexSize(CodedIndex.HasConstant) // Parent
                        + blobidx_size; // Value
                    break;
                case Table.CustomAttribute:
                    size = GetCodedIndexSize(CodedIndex.HasCustomAttribute) // Parent
                        + GetCodedIndexSize(CodedIndex.CustomAttributeType) // Type
                        + blobidx_size; // Value
                    break;
                case Table.FieldMarshal:
                    size = GetCodedIndexSize(CodedIndex.HasFieldMarshal)    // Parent
                        + blobidx_size; // NativeType
                    break;
                case Table.DeclSecurity:
                    size = 2    // Action
                        + GetCodedIndexSize(CodedIndex.HasDeclSecurity) // Parent
                        + blobidx_size; // PermissionSet
                    break;
                case Table.ClassLayout:
                    size = 6    // PackingSize 2, ClassSize 4
                        + GetTableIndexSize(Table.TypeDef); // Parent
                    break;
                case Table.FieldLayout:
                    size = 4    // Offset
                        + GetTableIndexSize(Table.Field);   // Field
                    break;
                case Table.StandAloneSig:
                    size = blobidx_size;    // Signature
                    break;
                case Table.EventMap:
                    size = GetTableIndexSize(Table.TypeDef) // Parent
                        + GetTableIndexSize(Table.Event);   // EventList
                    break;
                case Table.EventPtr:
                    size = GetTableIndexSize(Table.Event);  // Event
                    break;
                case Table.Event:
                    size = 2    // Flags
                        + stridx_size // Name
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef);   // EventType
                    break;
                case Table.PropertyMap:
                    size = GetTableIndexSize(Table.TypeDef) // Parent
                        + GetTableIndexSize(Table.Property);    // PropertyList
                    break;
                case Table.PropertyPtr:
                    size = GetTableIndexSize(Table.Property);   // Property
                    break;
                case Table.Property:
                    size = 2    // Flags
                        + stridx_size   // Name
                        + blobidx_size; // Type
                    break;
                case Table.MethodSemantics:
                    size = 2    // Semantics
                        + GetTableIndexSize(Table.Method)   // Method
                        + GetCodedIndexSize(CodedIndex.HasSemantics);   // Association
                    break;
                case Table.MethodImpl:
                    size = GetTableIndexSize(Table.TypeDef) // Class
                        + GetCodedIndexSize(CodedIndex.MethodDefOrRef)  // MethodBody
                        + GetCodedIndexSize(CodedIndex.MethodDefOrRef); // MethodDeclaration
                    break;
                case Table.ModuleRef:
                    size = stridx_size; // Name
                    break;
                case Table.TypeSpec:
                    size = blobidx_size;    // Signature
                    break;
                case Table.ImplMap:
                    size = 2    // MappingFlags
                        + GetCodedIndexSize(CodedIndex.MemberForwarded) // MemberForwarded
                        + stridx_size   // ImportName
                        + GetTableIndexSize(Table.ModuleRef);   // ImportScope
                    break;
                case Table.FieldRVA:
                    size = 4    // RVA
                        + GetTableIndexSize(Table.Field);   // Field
                    break;
                case Table.EncLog:
                    size = 8;
                    break;
                case Table.EncMap:
                    size = 4;
                    break;
                case Table.Assembly:
                    size = 16 // HashAlgId 4, Version 4 * 2, Flags 4
                        + blobidx_size  // PublicKey
                        + (stridx_size * 2);    // Name, Culture
                    break;
                case Table.AssemblyProcessor:
                    size = 4;   // Processor
                    break;
                case Table.AssemblyOS:
                    size = 12;  // Platform 4, Version 2 * 4
                    break;
                case Table.AssemblyRef:
                    size = 12   // Version 2 * 4 + Flags 4
                        + (blobidx_size * 2)    // PublicKeyOrToken, HashValue
                        + (stridx_size * 2);    // Name, Culture
                    break;
                case Table.AssemblyRefProcessor:
                    size = 4    // Processor
                        + GetTableIndexSize(Table.AssemblyRef); // AssemblyRef
                    break;
                case Table.AssemblyRefOS:
                    size = 12   // Platform 4, Version 2 * 4
                        + GetTableIndexSize(Table.AssemblyRef); // AssemblyRef
                    break;
                case Table.File:
                    size = 4    // Flags
                        + stridx_size   // Name
                        + blobidx_size; // HashValue
                    break;
                case Table.ExportedType:
                    size = 8    // Flags 4, TypeDefId 4
                        + (stridx_size * 2) // Name, Namespace
                        + GetCodedIndexSize(CodedIndex.Implementation); // Implementation
                    break;
                case Table.ManifestResource:
                    size = 8    // Offset, Flags
                        + stridx_size   // Name
                        + GetCodedIndexSize(CodedIndex.Implementation); // Implementation
                    break;
                case Table.NestedClass:
                    size = GetTableIndexSize(Table.TypeDef) // NestedClass
                        + GetTableIndexSize(Table.TypeDef); // EnclosingClass
                    break;
                case Table.GenericParam:
                    size = 4    // Number, Flags
                        + GetCodedIndexSize(CodedIndex.TypeOrMethodDef) // Owner
                        + stridx_size;  // Name
                    break;
                case Table.MethodSpec:
                    size = GetCodedIndexSize(CodedIndex.MethodDefOrRef) // Method
                        + blobidx_size; // Instantiation
                    break;
                case Table.GenericParamConstraint:
                    size = GetTableIndexSize(Table.GenericParam)    // Owner
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef);   // Constraint
                    break;
                case Table.Document:
                    size = blobidx_size // Name
                        + guididx_size  // HashAlgorithm
                        + blobidx_size  // Hash
                        + guididx_size; // Language
                    break;
                case Table.MethodDebugInformation:
                    size = GetTableIndexSize(Table.Document)  // Document
                        + blobidx_size; // SequencePoints
                    break;
                case Table.LocalScope:
                    size = GetTableIndexSize(Table.Method)  // Method
                        + GetTableIndexSize(Table.ImportScope)  // ImportScope
                        + GetTableIndexSize(Table.LocalVariable)    // VariableList
                        + GetTableIndexSize(Table.LocalConstant)    // ConstantList
                        + 4 * 2;    // StartOffset, Length
                    break;
                case Table.LocalVariable:
                    size = 2    // Attributes
                        + 2     // Index
                        + stridx_size;  // Name
                    break;
                case Table.LocalConstant:
                    size = stridx_size  // Name
                        + blobidx_size; // Signature
                    break;
                case Table.ImportScope:
                    size = GetTableIndexSize(Table.ImportScope) // Parent
                        + blobidx_size;
                    break;
                case Table.StateMachineMethod:
                    size = GetTableIndexSize(Table.Method) // MoveNextMethod
                        + GetTableIndexSize(Table.Method);  // KickOffMethod
                    break;
                case Table.CustomDebugInformation:
                    size = GetCodedIndexSize(CodedIndex.HasCustomDebugInformation) // Parent
                        + guididx_size  // Kind
                        + blobidx_size; // Value
                    break;
                default:
                    throw new NotSupportedException();
            }

            TableInfos[i].RowSize = size;

            var table = Add<MetadataTable>();
            table.Name = tableKind;
            for (int row = 0; row < TableInfos[i].RowCount; row++)
            {
                TableRow tableRow = null;
                if (tableKind == Table.Method)
                {
                    tableRow = new MethodTableRow
                    {
                        Length = size,
                        Name = new Node { Length = stridx_size },
                        Signature = new Node { Length = blobidx_size },
                        ParamList = new Node { Length = GetTableIndexSize(Table.Param) }
                    };
                }
                else
                {
                    tableRow = new TableRow { Length = size };
                }

                table.Add(tableRow);
                if (tableRow is MethodTableRow methodTableRow)
                {
                    FindMethod(methodTableRow.RVA.Value);
                }
            }

            tables.Add(table);
        }

        Tables = tables;
    }

    private void FindMethod(int rva)
    {
        if (rva == 0)
        {
            return;
        }

        var peFile = FindAncestor<PEFile>();
        var textSection = FindAncestor<Section>();
        var offset = peFile.ResolveVirtualAddress(rva);
        byte headerByte = peFile.Buffer.ReadByte(offset);
        byte twoBits = (byte)(headerByte & 3);
        if (twoBits == 2)
        {
            ReadTinyMethod(headerByte, offset + 1);
        }
        else
        {
            ReadFatMethod(headerByte, offset + 1);
        }
    }

    private void ReadFatMethod(byte header, int offset)
    {
    }

    private void ReadTinyMethod(byte header, int offset)
    {
        int codeSize = header >> 2;
        var tinyMethod = new TinyMethod
        {
            Start = offset - 1,
            CodeSize = codeSize
        };
        PEFile.Add(tinyMethod);
    }
}

public class TinyMethod : Node
{
    public override void Parse()
    {
        Header = AddOneByte();
        IL = new Node { Length = CodeSize };
        Add(IL);
    }

    public int CodeSize { get; set; }
    public OneByte Header { get; set; }
    public Node IL { get; set; }
}

public class Sequence : Node
{
}

public class TableRow : Node
{
}

public class MethodTableRow : TableRow
{
    public override void Parse()
    {
        RVA = AddFourBytes();
        ImplFlags = AddTwoBytes();
        Flags = AddTwoBytes();
        Add(Name);
        Add(Signature);
        Add(ParamList);
    }

    public FourBytes RVA { get; set; }
    public TwoBytes ImplFlags { get; set; }
    public TwoBytes Flags { get; set; }
    public Node Name { get; set; }
    public Node Signature { get; set; }
    public Node ParamList { get; set; }
}

public class MetadataTable : Sequence
{
    public Table Name { get; set; }
}

public enum Table : byte
{
    Module = 0x00,
    TypeRef = 0x01,
    TypeDef = 0x02,
    FieldPtr = 0x03,
    Field = 0x04,
    MethodPtr = 0x05,
    Method = 0x06,
    ParamPtr = 0x07,
    Param = 0x08,
    InterfaceImpl = 0x09,
    MemberRef = 0x0a,
    Constant = 0x0b,
    CustomAttribute = 0x0c,
    FieldMarshal = 0x0d,
    DeclSecurity = 0x0e,
    ClassLayout = 0x0f,
    FieldLayout = 0x10,
    StandAloneSig = 0x11,
    EventMap = 0x12,
    EventPtr = 0x13,
    Event = 0x14,
    PropertyMap = 0x15,
    PropertyPtr = 0x16,
    Property = 0x17,
    MethodSemantics = 0x18,
    MethodImpl = 0x19,
    ModuleRef = 0x1a,
    TypeSpec = 0x1b,
    ImplMap = 0x1c,
    FieldRVA = 0x1d,
    EncLog = 0x1e,
    EncMap = 0x1f,
    Assembly = 0x20,
    AssemblyProcessor = 0x21,
    AssemblyOS = 0x22,
    AssemblyRef = 0x23,
    AssemblyRefProcessor = 0x24,
    AssemblyRefOS = 0x25,
    File = 0x26,
    ExportedType = 0x27,
    ManifestResource = 0x28,
    NestedClass = 0x29,
    GenericParam = 0x2a,
    MethodSpec = 0x2b,
    GenericParamConstraint = 0x2c,

    Document = 0x30,
    MethodDebugInformation = 0x31,
    LocalScope = 0x32,
    LocalVariable = 0x33,
    LocalConstant = 0x34,
    ImportScope = 0x35,
    StateMachineMethod = 0x36,
    CustomDebugInformation = 0x37,
}

enum CodedIndex
{
    TypeDefOrRef,
    HasConstant,
    HasCustomAttribute,
    HasFieldMarshal,
    HasDeclSecurity,
    MemberRefParent,
    HasSemantics,
    MethodDefOrRef,
    MemberForwarded,
    Implementation,
    CustomAttributeType,
    ResolutionScope,
    TypeOrMethodDef,
    HasCustomDebugInformation,
}

public class UncompressedMetadataTableStream : MetadataStream
{
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
            EmbeddedPdb = (EmbeddedPdb)Parent
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
                else if (node.Start >= child.Start && node.Start < child.End)
                {
                    child.Add(node);
                    return;
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

        // if Length has been preset previously, keep the old value
        int newLength = LastChildEnd - Start;
        if (newLength > Length)
        {
            Length = newLength;
        }
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

    public static int GetSize(this CodedIndex self, Func<Table, int> counter)
    {
        int bits;
        Table[] tables;

        switch (self)
        {
            case CodedIndex.TypeDefOrRef:
                bits = 2;
                tables = new[] { Table.TypeDef, Table.TypeRef, Table.TypeSpec };
                break;
            case CodedIndex.HasConstant:
                bits = 2;
                tables = new[] { Table.Field, Table.Param, Table.Property };
                break;
            case CodedIndex.HasCustomAttribute:
                bits = 5;
                tables = new[] {
                    Table.Method, Table.Field, Table.TypeRef, Table.TypeDef, Table.Param, Table.InterfaceImpl, Table.MemberRef,
                    Table.Module, Table.DeclSecurity, Table.Property, Table.Event, Table.StandAloneSig, Table.ModuleRef,
                    Table.TypeSpec, Table.Assembly, Table.AssemblyRef, Table.File, Table.ExportedType,
                    Table.ManifestResource, Table.GenericParam, Table.GenericParamConstraint, Table.MethodSpec,
                };
                break;
            case CodedIndex.HasFieldMarshal:
                bits = 1;
                tables = new[] { Table.Field, Table.Param };
                break;
            case CodedIndex.HasDeclSecurity:
                bits = 2;
                tables = new[] { Table.TypeDef, Table.Method, Table.Assembly };
                break;
            case CodedIndex.MemberRefParent:
                bits = 3;
                tables = new[] { Table.TypeDef, Table.TypeRef, Table.ModuleRef, Table.Method, Table.TypeSpec };
                break;
            case CodedIndex.HasSemantics:
                bits = 1;
                tables = new[] { Table.Event, Table.Property };
                break;
            case CodedIndex.MethodDefOrRef:
                bits = 1;
                tables = new[] { Table.Method, Table.MemberRef };
                break;
            case CodedIndex.MemberForwarded:
                bits = 1;
                tables = new[] { Table.Field, Table.Method };
                break;
            case CodedIndex.Implementation:
                bits = 2;
                tables = new[] { Table.File, Table.AssemblyRef, Table.ExportedType };
                break;
            case CodedIndex.CustomAttributeType:
                bits = 3;
                tables = new[] { Table.Method, Table.MemberRef };
                break;
            case CodedIndex.ResolutionScope:
                bits = 2;
                tables = new[] { Table.Module, Table.ModuleRef, Table.AssemblyRef, Table.TypeRef };
                break;
            case CodedIndex.TypeOrMethodDef:
                bits = 1;
                tables = new[] { Table.TypeDef, Table.Method };
                break;
            case CodedIndex.HasCustomDebugInformation:
                bits = 5;
                tables = new[] {
                    Table.Method, Table.Field, Table.TypeRef, Table.TypeDef, Table.Param, Table.InterfaceImpl, Table.MemberRef,
                    Table.Module, Table.DeclSecurity, Table.Property, Table.Event, Table.StandAloneSig, Table.ModuleRef,
                    Table.TypeSpec, Table.Assembly, Table.AssemblyRef, Table.File, Table.ExportedType,
                    Table.ManifestResource, Table.GenericParam, Table.GenericParamConstraint, Table.MethodSpec,
                    Table.Document, Table.LocalScope, Table.LocalVariable, Table.LocalConstant, Table.ImportScope,
                };
                break;
            default:
                throw new ArgumentException();
        }

        int max = 0;

        for (int i = 0; i < tables.Length; i++)
        {
            max = System.Math.Max(counter(tables[i]), max);
        }

        return max < (1 << (16 - bits)) ? 2 : 4;
    }
}

public record struct Span(int Start, int Length);