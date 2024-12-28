using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class OptionalHeader : Node
{
    public OptionalHeader(short sizeOfOptionalHeader)
    {
        Text = "Optional Header";
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

        Text = $"Optional header ({(isPE32Plus ? "64-bit" : "32-bit")})";
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
        Text = "Standard fields";
    }

    public override void Parse()
    {
        Magic = AddTwoBytes("Magic");
        IsPE32Plus = Magic.ReadInt16() == 0x20B;

        MajorLinkerVersion = AddOneByte("Major linker version");
        MinorLinkerVersion = AddOneByte("Minor linker version");
        SizeOfCode = AddFourBytes("Size of code");
        SizeOfInitializedData = AddFourBytes("Size of initialized data");
        SizeOfUninitializedData = AddFourBytes("Size of uninitialized data");
        AddressOfEntryPoint = AddFourBytes("Address of entrypoint");
        BaseOfCode = AddFourBytes("Base of code");

        if (!IsPE32Plus)
        {
            BaseOfData = AddFourBytes("Base of data");
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
        Text = "Windows fields";
    }

    public bool IsPE32Plus { get; }

    public override void Parse()
    {
        ImageBase = AddFourOrEightBytes(IsPE32Plus);
        ImageBase.Text = "Image base";
        SectionAlignment = AddFourBytes("Section alignment");
        FileAlignment = AddFourBytes("File alignment");
        MajorOperationSystemVersion = AddTwoBytes("Major OS version");
        MinorOperatingSystemVersion = AddTwoBytes("Minor OS version");
        MajorImageVersion = AddTwoBytes("Major image version");
        MinorImageVersion = AddTwoBytes("Minor image version");
        MajorSubsystemVersion = AddTwoBytes("Major subsystem version");
        MinorSubsystemVersion = AddTwoBytes("Minor subsystem version");
        Win32VersionValue = AddFourBytes("Win32 version value");
        SizeOfImage = AddFourBytes("Size of image");
        SizeOfHeaders = AddFourBytes("Size of headers");
        Checksum = AddFourBytes("Checksum");
        Subsystem = AddTwoBytes("Subsystem");
        DllCharacteristics = AddTwoBytes("Dll characteristics");
        SizeOfStackReserve = AddFourOrEightBytes(IsPE32Plus);
        SizeOfStackReserve.Text = "Size of stack reserve";
        SizeOfStackCommit = AddFourOrEightBytes(IsPE32Plus);
        SizeOfStackCommit.Text = "Size of stack commit";
        SizeOfHeapReserve = AddFourOrEightBytes(IsPE32Plus);
        SizeOfHeapReserve.Text = "Size of heap reserve";
        SizeOfHeapCommit = AddFourOrEightBytes(IsPE32Plus);
        SizeOfHeapCommit.Text = "Size of heap commit";
        LoaderFlags = AddFourBytes("Loader flags");
        NumberOfRvaAndSizes = AddFourBytes("Number of RVA and sizes");
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
        Text = "Data directories";
    }

    public override void Parse()
    {
        ExportTable = Add<DataDirectory>("Export table");
        ImportTable = Add<DataDirectory>("Import table");
        ResourceTable = Add<DataDirectory>("Resource table");
        ExceptionTable = Add<DataDirectory>("Exception table");
        CertificateTable = Add<DataDirectory>("Certificate table");
        BaseRelocationTable = Add<DataDirectory>("Base relocation table");
        Debug = Add<DataDirectory>("Debug");
        Architecture = Add<DataDirectory>("Architecture");
        GlobalPtr = Add<DataDirectory>("Global Ptr");
        TLSTable = Add<DataDirectory>("Thread Local Storage table");
        LoadConfigTable = Add<DataDirectory>("Load config table");
        BoundImport = Add<DataDirectory>("Bound import");
        IAT = Add<DataDirectory>("Import Address table");
        DelayImportDescriptor = Add<DataDirectory>("Delay Import Descriptor");
        CLRRuntimeHeader = Add<DataDirectory>(".NET CLR runtime header");
        ReservedZero = Add<DataDirectory>("Reserved zero");
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

