using System.IO.Compression;
using System.IO;

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
        var decompressedSize = DecompressedSize.Value;
        CompressedStream = new CompressedDeflateStream
        {
            Length = length - 8
        };
        Add(CompressedStream);

        var bytes = Buffer.ReadBytes(CompressedStream.Start, CompressedStream.Length);
        var compressedMemoryStream = new MemoryStream(bytes);
        var deflateStream = new DeflateStream(compressedMemoryStream, CompressionMode.Decompress, leaveOpen: true);
        DecompressedStream = new MemoryStream(decompressedSize);
        deflateStream.CopyTo(DecompressedStream);

        var metadataBuffer = new StreamBuffer(DecompressedStream);
        Metadata = new Metadata
        {
            Buffer = metadataBuffer,
            Start = 0,
            Length = (int)DecompressedStream.Length,
            EmbeddedPdb = this
        };
        Metadata.Parse();
    }

    public FourBytes MPDB { get; set; }
    public FourBytes DecompressedSize { get; set; }
    public CompressedDeflateStream CompressedStream { get; set; }
    public MemoryStream DecompressedStream { get; set; }
    public Metadata Metadata { get; set; }
}
