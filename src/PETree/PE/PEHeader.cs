using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

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
}

public class IAT : Node
{
    public IAT()
    {
        Text = "Import Address Table";
    }
}

