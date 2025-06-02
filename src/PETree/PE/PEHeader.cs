using System.Collections.Generic;

namespace GuiLabs.FileFormat.PE;

public class PEHeader : Node
{
    public PEHeader()
    {
        Text = "PE Header";
    }

    public override void Parse()
    {
        PEHeaderSignature = AddFourBytes("PE Header signature");

        Platform = AddTwoBytes("Platform");
        NumberOfSections = AddTwoBytes("Number of sections");
        TimeDateStamp = AddFourBytes("Time-date stamp");
        PointerToSymbolTable = AddFourBytes("Pointer to symbol table");
        NumberOfSymbols = AddFourBytes("Number of symbols");
        SizeOfOptionalHeader = AddTwoBytes("Size of optional header");
        Characteristics = AddTwoBytes("Characteristics");
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

public class DataDirectory : EightBytes
{
    public DataDirectory() : base()
    {
        Text = "Data Directory";
    }

    public override void Parse()
    {
        RVA = AddFourBytes("RVA");
        Size = AddFourBytes("Size");
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
}

public class CertificateTable : Node
{
    public CertificateTable()
    {
        Text = "Certificate table";
    }
}

public class BoundImport : Node
{
    public BoundImport()
    {
        Text = "Bound import";
    }
}

public class ImportTable : Node
{
    public ImportTable()
    {
        Text = "Import table";
    }

    public override void Parse()
    {
        var list = new List<ImportsDirectory>();
        ImportsDirectory importsDirectory;
        do
        {
            importsDirectory = Add<ImportsDirectory>("Imports directory");
            list.Add(importsDirectory);
        } while (!Buffer.IsZeroFilled(importsDirectory.Span));

        ImportsDirectories = list.ToArray();
    }

    public IReadOnlyList<ImportsDirectory> ImportsDirectories { get; set; }
}

public class ImportsDirectory : Node
{
    public override void Parse()
    {
        LookupTableRVA = AddFourBytes("Lookup table RVA");
        TimeDateStamp = AddFourBytes("TimeDate stamp");
        ForwarderChain = AddFourBytes("Forwarder chain");
        DllNameRVA = AddFourBytes("Dll name RVA");
        AddressTableRVA = AddFourBytes("Address table RVA");
    }

    public FourBytes LookupTableRVA { get; set; }
    public FourBytes TimeDateStamp { get; set; }
    public FourBytes ForwarderChain { get; set; }
    public FourBytes DllNameRVA { get; set; }
    public FourBytes AddressTableRVA { get; set; }
}

public class IAT : Node
{
    public IAT()
    {
        Text = "Import Address Table";
    }

    public override void Parse()
    {
        var list = new List<FourBytes>();
        FourBytes entry;
        do
        {
            entry = Add<FourBytes>("Entry");
            list.Add(entry);
        } while (!Buffer.IsZeroFilled(entry.Span));

        Entries = list.ToArray();
    }

    public IReadOnlyList<FourBytes> Entries { get; set; }
}

public class ImageImportByName : Node
{
    public ImageImportByName()
    {
        Text = "IMAGE_IMPORT_BY_NAME";
    }

    public override void Parse()
    {
        Hint = AddTwoBytes("Hint");
        Name = Add<ZeroTerminatedString>("Name");
    }

    public TwoBytes Hint { get; set; }
    public ZeroTerminatedString Name { get; set; }
}

public class ImportLookupTable : Node
{
    public ImportLookupTable()
    {
        Text = "Import Lookup Table";
    }

    public override void Parse()
    {
        var list = new List<FourBytes>();
        FourBytes entry;
        do
        {
            entry = Add<FourBytes>("Entry");
            list.Add(entry);
        } while (!Buffer.IsZeroFilled(entry.Span));

        Entries = list.ToArray();
    }

    public IReadOnlyList<FourBytes> Entries { get; set; }
}