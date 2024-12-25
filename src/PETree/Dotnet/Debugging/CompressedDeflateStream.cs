using System.IO;
using System.IO.Compression;
using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class CompressedDeflateStream : Node
{
    public override void Parse()
    {
        var bytes = Buffer.ReadBytes(Start, Length);
        var compressedMemoryStream = new MemoryStream(bytes);
        var deflateStream = new DeflateStream(compressedMemoryStream, CompressionMode.Decompress, leaveOpen: true);
        var decompressedStream = new MemoryStream(DecompressedSize);
        deflateStream.CopyTo(decompressedStream);

        var metadataBuffer = new StreamBuffer(decompressedStream);
        PdbMetadata = new Metadata
        {
            Buffer = metadataBuffer,
            Start = 0,
            Length = (int)decompressedStream.Length,
            EmbeddedPdb = (EmbeddedPdb)Parent
        };
        PdbMetadata.Parse();
    }

    public int DecompressedSize { get; set; }
    public Metadata PdbMetadata { get; set; }
}
