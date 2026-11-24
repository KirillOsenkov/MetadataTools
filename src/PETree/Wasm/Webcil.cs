using System.Collections.Generic;
using GuiLabs.FileFormat.PE;

namespace GuiLabs.FileFormat.Wasm;

/// <summary>
/// A .NET assembly in the Webcil format: the payload of the WebAssembly
/// module that a browser build ships in place of a .dll. It has a short
/// header and section headers in place of the PE headers; the CLI header,
/// the metadata, the IL and the resources are the same as in a PE file.
/// The image is read as a PE image without PE headers, so the same readers
/// are used for those parts.
/// </summary>
public class WebcilImage : PEFile
{
    public const uint Signature = 0x4C496257; // "WbIL"

    public WebcilImage(ByteBuffer buffer) : base(buffer)
    {
    }

    public override bool IsPE32Plus => false;

    public override void Parse()
    {
        int majorVersion = Buffer.ReadUInt16(Start + 4);
        int minorVersion = Buffer.ReadUInt16(Start + 6);

        var fields = new List<(string, int)>
        {
            ("WbIL signature", 4),
            ("Major version", 2),
            ("Minor version", 2),
            ("Section count", 2),
            (majorVersion >= 1 ? "Relocation section index" : "Reserved", 2),
            ("CLI header RVA", 4),
            ("CLI header size", 4),
            ("Debug directory RVA", 4),
            ("Debug directory size", 4)
        };
        if (majorVersion >= 1)
        {
            fields.Add(("Table base", 4));
        }

        var header = new Record { Start = Start, Fields = fields.ToArray(), Text = "Webcil header" };
        Add(header);

        short sectionCount = Buffer.ReadInt16(Start + 8);
        int cliHeaderRva = header.ReadInt32(5);
        int debugRva = header.ReadInt32(7);
        int debugSize = header.ReadInt32(8);

        var sectionTable = new WebcilSectionTable(sectionCount, Start);
        Add(sectionTable);
        SectionTable = sectionTable;

        var sections = new List<Section>();
        for (int i = 0; i < sectionTable.SectionHeaders.Count; i++)
        {
            var sectionHeader = sectionTable.SectionHeaders[i];
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            if (size <= 0 || raw < sectionTable.End || (long)raw + size > End)
            {
                continue;
            }

            var section = new Section { Start = raw, Length = size, Text = sectionHeader.Name.Text };
            Add(section);
            sections.Add(section);
        }

        int cliHeader = cliHeaderRva != 0 ? ResolveVirtualAddress(cliHeaderRva) : 0;
        if (cliHeader > 0)
        {
            ReadDotnetMetadata(cliHeader);
        }

        int debugDirectory = debugRva != 0 && debugSize > 0 ? ResolveVirtualAddress(debugRva) : 0;
        if (debugDirectory > 0)
        {
            AddDebugDirectory(debugDirectory, debugSize, fileOffsetBase: Start);
        }

        foreach (var section in sections)
        {
            section.AddRemainingPadding();
        }

        ClassifyUncoveredSpans();
        Text = $"Webcil image {majorVersion}.{minorVersion}";
    }
}

public class WebcilSectionTable : SectionTable
{
    private readonly int imageStart;

    public WebcilSectionTable(short count, int imageStart) : base(count)
    {
        this.imageStart = imageStart;
    }

    public override void Parse()
    {
        var list = new List<SectionHeader>();
        for (int i = 0; i < Count; i++)
        {
            var sectionHeader = new WebcilSectionHeader { ImageStart = imageStart, Index = i };
            Add(sectionHeader);
            list.Add(sectionHeader);
        }

        SectionHeaders = list;
    }
}

/// <summary>
/// The four fields that a Webcil section header keeps of a PE section
/// header. The pointer to the raw data is relative to the start of the
/// Webcil image; its node reports the file offset.
/// </summary>
public class WebcilSectionHeader : SectionHeader
{
    public int ImageStart { get; set; }
    public int Index { get; set; }

    public override void Parse()
    {
        VirtualSize = AddFourBytes("Virtual size");
        VirtualAddress = AddFourBytes("Virtual address");
        SizeOfRawData = AddFourBytes("Size of raw data");
        PointerToRawData = new OffsetFourBytes { Offset = ImageStart, Text = "Pointer to raw data (from the start of the Webcil image)" };
        Add(PointerToRawData);

        // A Webcil section has no name and no flags. These two are not part
        // of the tree; they are here for the code that reads them.
        const int InitializedDataReadable = 0x40000040;
        Name = new EightByteString { Text = $"Webcil section {Index}" };
        Characteristics = new ConstantFourBytes { Constant = InitializedDataReadable };

        Text = $"Webcil section {Index} header";
    }
}

public class OffsetFourBytes : FourBytes
{
    public int Offset { get; set; }

    public override int ReadInt32() => base.ReadInt32() + Offset;
}

public class ConstantFourBytes : FourBytes
{
    public int Constant { get; set; }

    public override int ReadInt32() => Constant;
}
