using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class MetadataStreamHeader : Node
{
    public MetadataStreamHeader()
    {
        Text = "Metadata stream header";
    }

    public override void Parse()
    {
        Offset = AddFourBytes("Offset");
        Size = AddFourBytes("Size");
        Name = Add<ZeroTerminatedAlignedString>();
        Text = $"{Name.Text} metadata stream";
    }

    public FourBytes Offset { get; set; }
    public FourBytes Size { get; set; }
    public ZeroTerminatedString Name { get; set; }
}
