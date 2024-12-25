using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

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

