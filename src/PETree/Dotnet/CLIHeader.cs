using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class CLIHeader : Node
{
    public CLIHeader()
    {
        Text = ".NET CLI Header";
    }

    public override void Parse()
    {
        Cb = AddFourBytes("Cb");
        MajorRuntimeVersion = AddTwoBytes("Major runtime version");
        MinorRuntimeVersion = AddTwoBytes("Minor runtime version");
        Metadata = Add<DataDirectory>("Metadata");
        Flags = AddFourBytes("Flags");
        EntryPointToken = AddFourBytes("Entrypoint token");
        Resources = Add<DataDirectory>("Resources");
        StrongNameSignature = Add<DataDirectory>("Strong name signature");
        CodeManagerTable = AddEightBytes("Code manager table");
        VTableFixups = AddEightBytes("VTable fixups");
        ExportAddressTableJumps = AddEightBytes("Export address table jumps");
        ManagedNativeHeader = AddEightBytes("Managed native header");
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
