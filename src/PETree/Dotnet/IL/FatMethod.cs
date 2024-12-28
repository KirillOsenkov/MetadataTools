using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class FatMethod : Node
{
    public FatMethod()
    {
        Text = "Fat method";
    }

    public override void Parse()
    {
        Header = AddTwoBytes("Header");
        MaxStack = AddTwoBytes("Max stack");
        CodeSize = AddFourBytes("Code size");
        LocalVarSignatureToken = AddFourBytes("Local variable signature token");
        ILCode = new Node { Length = CodeSize.Value, Text = "IL instructions" };
        Add(ILCode);

        if ((Header.Value & 8) != 0)
        {
            AddSection();
        }
    }

    private void AddSection()
    {
        AddAlignedPadding(4);

        const byte fat_format = 0x40;
        const byte more_sects = 0x80;

        var flags = AddOneByte("Flags");
        if ((flags.Value & fat_format) == 0)
        {
            AddSmallSection();
        }
        else
        {
            AddFatSection();
        }

        if ((flags.Value & more_sects) != 0)
        {
            AddSection();
        }
    }

    private void AddFatSection()
    {
        var integer = AddThreeBytes("Size");
        AddBytes(integer.Value - 4, "Fat section");
    }

    private void AddSmallSection()
    {
        var count = AddOneByte("Count").Value;
        AddTwoBytes("Ignored");
        AddBytes(count - 4, "Small section");
    }

    public TwoBytes Header { get; set; }
    public TwoBytes MaxStack { get; set; }
    public FourBytes CodeSize { get; set; }
    public FourBytes LocalVarSignatureToken { get; set; }
    public Node ILCode { get; set; }
}
