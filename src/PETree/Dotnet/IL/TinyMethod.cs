using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class TinyMethod : Node
{
    public override void Parse()
    {
        Header = AddOneByte();
        IL = new Node { Length = CodeSize };
        Add(IL);
    }

    public int CodeSize { get; set; }
    public OneByte Header { get; set; }
    public Node IL { get; set; }
}
