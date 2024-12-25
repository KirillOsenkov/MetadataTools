using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class FatMethod : Node
{
    public override void Parse()
    {
        Header = AddTwoBytes();
        MaxStack = AddTwoBytes();
        CodeSize = AddFourBytes();
        LocalVarSignatureToken = AddFourBytes();
        ILCode = new Node { Length = CodeSize.Value };
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

        var flags = AddOneByte();
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
        var first = AddOneByte();
        var second = AddOneByte();
        var third = AddOneByte();
        var integer = (third.Value << 16) + (second.Value << 8) + first.Value;

        AddBytes(integer - 4);
    }

    private void AddSmallSection()
    {
        var count = AddOneByte().Value;
        AddTwoBytes();
        AddBytes(count - 4);
    }

    public TwoBytes Header { get; set; }
    public TwoBytes MaxStack { get; set; }
    public FourBytes CodeSize { get; set; }
    public FourBytes LocalVarSignatureToken { get; set; }
    public Node ILCode { get; set; }
}
