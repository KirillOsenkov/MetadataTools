using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class TinyMethod : Node
{
    public TinyMethod()
    {
        Text = "Tiny method";
    }

    public override void Parse()
    {
        Header = AddOneByte("Header");
        IL = new Node { Length = CodeSize, Text = "IL instructions" };
        Add(IL);
    }

    public int CodeSize { get; set; }
    public OneByte Header { get; set; }
    public Node IL { get; set; }
}
