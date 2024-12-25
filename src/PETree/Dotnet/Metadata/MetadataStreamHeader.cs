using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class MetadataStreamHeader : Node
{
    public override void Parse()
    {
        Offset = AddFourBytes();
        Size = AddFourBytes();
        Name = Add<ZeroTerminatedAlignedString>();
    }

    public FourBytes Offset { get; set; }
    public FourBytes Size { get; set; }
    public ZeroTerminatedString Name { get; set; }
}
