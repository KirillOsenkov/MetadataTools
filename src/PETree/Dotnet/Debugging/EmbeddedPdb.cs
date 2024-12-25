using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class EmbeddedPdb : Node
{
    public override void Parse()
    {
        var length = Length;

        MPDB = AddFourBytes();
        DecompressedSize = AddFourBytes();
        CompressedStream = new CompressedDeflateStream
        {
            Length = length - 8,
            DecompressedSize = DecompressedSize.Value
        };
        Add(CompressedStream);
    }

    public FourBytes MPDB { get; set; }
    public FourBytes DecompressedSize { get; set; }
    public CompressedDeflateStream CompressedStream { get; set; }
}
