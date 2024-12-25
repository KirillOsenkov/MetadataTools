using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

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
