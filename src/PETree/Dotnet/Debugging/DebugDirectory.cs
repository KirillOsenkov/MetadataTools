using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class DebugDirectoryEntry : Node
{
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
