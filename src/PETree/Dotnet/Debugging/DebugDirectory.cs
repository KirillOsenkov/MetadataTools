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
        OmapToSrc = 7,
        OmapFromSrc = 8,
        Borland = 9,
        Reserved10 = 10,
        Clsid = 11,
        VcFeature = 12,
        Pogo = 13,
        Iltcg = 14,
        Mpx = 15,
        Reproducible = 16,
        EmbeddedPortablePdb = 17,
        PdbChecksum = 19,
        ExtendedDllCharacteristics = 20,
    }
}

public class PogoDebugData : DebugDirectoryEntry
{
    public PogoDebugData()
    {
        Text = "POGO data (section contributions)";
    }

    public override void Parse()
    {
        Signature = AddFourBytes("Signature");

        while (LastChildEnd + 8 < End)
        {
            Add<PogoEntry>();
        }

        AddRemainingPadding();
    }

    public FourBytes Signature { get; set; }
}

public class PogoEntry : Node
{
    public override void Parse()
    {
        RVA = AddFourBytes("RVA");
        Size = AddFourBytes("Size");
        Name = Add<ZeroTerminatedString>("Name");

        int aligned = (LastChildEnd - Start + 3) & ~3;
        if (Start + aligned > LastChildEnd)
        {
            AddPadding(Start + aligned - LastChildEnd);
        }

        Text = $"{Name.Text} RVA 0x{RVA.Value:X} ({Size.Value} bytes)";
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
    public ZeroTerminatedString Name { get; set; }
}

public class PdbChecksumDebugData : DebugDirectoryEntry
{
    public PdbChecksumDebugData()
    {
        Text = "PDB checksum";
    }

    public override void Parse()
    {
        AlgorithmName = Add<ZeroTerminatedString>("Algorithm");
        int hashLength = End - LastChildEnd;
        if (hashLength > 0)
        {
            Checksum = AddBytes(hashLength, "Checksum");
        }

        Text = $"PDB checksum ({AlgorithmName.Text})";
    }

    public ZeroTerminatedString AlgorithmName { get; set; }
    public Node Checksum { get; set; }
}

public class ExtendedDllCharacteristicsDebugData : DebugDirectoryEntry
{
    public ExtendedDllCharacteristicsDebugData()
    {
        Text = "Extended DLL characteristics";
    }

    public override void Parse()
    {
        Characteristics = AddFourBytes("Characteristics");

        int value = Characteristics.Value;
        var flags = new System.Collections.Generic.List<string>();
        if ((value & 0x01) != 0)
        {
            flags.Add("CET compatible");
        }

        if ((value & 0x40) != 0)
        {
            flags.Add("Forward CFI compatible");
        }

        if ((value & 0x80) != 0)
        {
            flags.Add("Hotpatch compatible");
        }

        if (flags.Count > 0)
        {
            Text = $"Extended DLL characteristics: {string.Join(", ", flags)}";
        }
    }

    public FourBytes Characteristics { get; set; }
}

public class VcFeatureDebugData : DebugDirectoryEntry
{
    public VcFeatureDebugData()
    {
        Text = "VC feature data";
    }

    public override void Parse()
    {
        AddFourBytes("Pre-VC++ 11 count");
        AddFourBytes("C/C++ count");
        AddFourBytes("/GS count");
        AddFourBytes("/sdl count");
        AddFourBytes("GuardN count");
        AddRemainingPadding();
    }
}
