namespace GuiLabs.FileFormat.PE.Dotnet;

public class DebugDirectoryEntry : Node
{
    public DebugDirectoryEntry()
    {
        Text = "Debug directory entry";
    }
}

public class RSDS : DebugDirectoryEntry
{
    public RSDS()
    {
        Text = "CodeView RSDS";
    }

    public override void Parse()
    {
        RSDSSignature = AddFourBytes("RSDS Signature");
        Guid = Add<SixteenBytes>("Guid");
        Age = AddFourBytes("Age");
        Path = Add<ZeroTerminatedString>("Path");
    }

    public FourBytes RSDSSignature { get; set; }
    public SixteenBytes Guid { get; set; }
    public FourBytes Age { get; set; }
    public ZeroTerminatedString Path { get; set; }
}

public class DebugDirectory : Node
{
    public override void Parse()
    {
        Characteristics = AddFourBytes("Characteristics");
        Timestamp = AddFourBytes("Timestamp");
        MajorVersion = AddTwoBytes("Major version");
        MinorVersion = AddTwoBytes("Minor version");
        Type = AddFourBytes("Type");
        SizeOfData = AddFourBytes("Size of data");
        AddressOfRawData = AddFourBytes("Address of raw data");
        PointerToRawData = AddFourBytes("Pointer to raw data");

        DirectoryType = (ImageDebugType)Type.Value;

        Text = $"{DirectoryType}";
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
