using System.Linq;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    /// <summary>
    /// An image packed with UPX has sections named UPX0 and UPX1. The pack
    /// header is located right before the first section data, and the
    /// compressed image starts at the beginning of UPX1.
    /// </summary>
    private void AddUpxData()
    {
        const int PackHeaderSize = 32;

        var packed = SectionTable.SectionHeaders.FirstOrDefault(s =>
            s.Name.Text.StartsWith("UPX", System.StringComparison.Ordinal) &&
            s.PointerToRawData.Value > 0 &&
            s.SizeOfRawData.Value > 0);
        if (packed == null)
        {
            return;
        }

        int raw = packed.PointerToRawData.Value;
        int searchStart = SectionTable.End;
        if (raw > Buffer.Length || raw - searchStart < PackHeaderSize)
        {
            return;
        }

        var bytes = Buffer.ReadBytes(searchStart, raw - searchStart);
        int header = -1;
        for (int i = bytes.Length - PackHeaderSize; i >= 0; i--)
        {
            if (bytes[i] == 'U' && bytes[i + 1] == 'P' && bytes[i + 2] == 'X' && bytes[i + 3] == '!')
            {
                header = searchStart + i;
                break;
            }
        }

        if (header < 0)
        {
            return;
        }

        int index = FindChildIndex(header);
        if (index >= 0)
        {
            return;
        }

        index = ~index;
        if (index < Children.Count && Children[index].Start < header + PackHeaderSize)
        {
            return;
        }

        var packHeader = new Record
        {
            Start = header,
            Length = PackHeaderSize,
            Fields =
            [
                ("UPX! signature", 4),
                ("Version", 1),
                ("Format", 1),
                ("Method", 1),
                ("Level", 1),
                ("Checksum of uncompressed data", 4),
                ("Checksum of compressed data", 4),
                ("Uncompressed size", 4),
                ("Compressed size", 4),
                ("Uncompressed file size", 4),
                ("Filter", 1),
                ("Filter parameter", 1),
                ("Unknown", 1),
                ("Header checksum", 1)
            ]
        };
        Add(packHeader);
        packHeader.Text = "UPX pack header";

        int compressedSize = packHeader.ReadInt32(8);
        int entryRva = OptionalHeader.StandardFields.AddressOfEntryPoint.Value;
        int entry = entryRva > 0 ? ResolveVirtualAddress(entryRva) : 0;
        if (compressedSize <= 0 || entry <= raw || raw + (long)compressedSize > entry)
        {
            return;
        }

        var compressedData = new Region { Start = raw, Length = compressedSize, Text = "UPX compressed data" };
        if (TryAdd(compressedData))
        {
            this.compressedData = compressedData;
        }
    }

    private Region compressedData;

    public bool IsCompressedData(int offset)
    {
        return compressedData != null && offset >= compressedData.Start && offset < compressedData.End;
    }
}
