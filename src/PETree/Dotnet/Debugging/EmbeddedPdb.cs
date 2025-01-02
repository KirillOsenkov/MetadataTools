namespace GuiLabs.FileFormat.PE.Dotnet;

public class EmbeddedPdb : Node
{
    public EmbeddedPdb()
    {
        Text = "Embedded PDB";
    }

    public override void Parse()
    {
        var length = Length;

        MPDB = AddFourBytes("MPDB header signature");
        DecompressedSize = AddFourBytes("Decompressed size");
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
