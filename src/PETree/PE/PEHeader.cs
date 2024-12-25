using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

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

public class CertificateTable : Node
{
}

public class BoundImport : Node
{
}

public class ImportTable : Node
{
}

public class IAT : Node
{
}

