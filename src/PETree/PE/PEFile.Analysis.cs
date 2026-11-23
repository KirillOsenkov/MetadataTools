using System;
using System.Collections.Generic;
using System.Text;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    public int PointerSize => IsPE32Plus ? 8 : 4;

    public int Machine => PEHeader.Platform.Value & 0xFFFF;

    public const int MachineI386 = 0x014C;
    public const int MachineAmd64 = 0x8664;
    public const int MachineArm64 = 0xAA64;
    public const int MachineArmNT = 0x01C4;

    private ulong imageBase;
    private bool imageBaseRead;

    public ulong ImageBase
    {
        get
        {
            if (!imageBaseRead)
            {
                int start = OptionalHeader.WindowsFields.ImageBase.Start;
                imageBase = IsPE32Plus ? Buffer.ReadUInt64(start) : Buffer.ReadUInt32(start);
                imageBaseRead = true;
            }

            return imageBase;
        }
    }

    public uint SizeOfImage => (uint)OptionalHeader.WindowsFields.SizeOfImage.Value;

    public ulong ReadPointer(int offset)
    {
        return IsPE32Plus ? Buffer.ReadUInt64(offset) : Buffer.ReadUInt32(offset);
    }

    /// <summary>
    /// The RVA of a virtual address, or 0 when the address is outside the image.
    /// </summary>
    public int VirtualAddressToRva(ulong virtualAddress)
    {
        ulong imageBase = ImageBase;
        if (virtualAddress <= imageBase || virtualAddress - imageBase >= SizeOfImage)
        {
            return 0;
        }

        return (int)(virtualAddress - imageBase);
    }

    /// <summary>
    /// The file offset of a virtual address, or 0 when it has no file data.
    /// </summary>
    public int ResolvePointer(ulong virtualAddress)
    {
        int rva = VirtualAddressToRva(virtualAddress);
        return rva == 0 ? 0 : ResolveVirtualAddress(rva);
    }

    /// <summary>
    /// The file offset stored as a pointer at the given file offset, or 0.
    /// </summary>
    public int ResolvePointerAt(int offset)
    {
        if (offset <= 0 || offset + PointerSize > Buffer.Length)
        {
            return 0;
        }

        return ResolvePointer(ReadPointer(offset));
    }

    public int OffsetToRva(int offset)
    {
        var sections = SectionTable.SectionHeaders;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            int raw = section.PointerToRawData.Value;
            if (raw != 0 && offset >= raw && offset < raw + section.SizeOfRawData.Value)
            {
                return offset - raw + section.VirtualAddress.Value;
            }
        }

        return 0;
    }

    public bool IsCodeAddress(int rva)
    {
        const uint ImageScnCntCode = 0x00000020;
        const uint ImageScnMemExecute = 0x20000000;
        var section = GetSectionAtVirtualAddress(rva);
        return section != null && ((uint)section.Characteristics.Value & (ImageScnCntCode | ImageScnMemExecute)) != 0;
    }

    /// <summary>
    /// Adds a node at the root when its byte range is inside a section and
    /// free of other nodes. Start and Length must be set.
    /// </summary>
    public bool TryAdd(Node node)
    {
        if (!IsRangeAvailable(node.Start, node.Length))
        {
            return false;
        }

        Add(node);
        return true;
    }

    public string ReadZeroTerminatedAscii(int offset, int maxLength = 512)
    {
        if (offset <= 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (int i = offset; i < Buffer.Length && sb.Length < maxLength; i++)
        {
            byte b = Buffer.ReadByte(i);
            if (b == 0)
            {
                return sb.ToString();
            }

            if (b < 0x20 || b > 0x7E)
            {
                return null;
            }

            sb.Append((char)b);
        }

        return null;
    }

    /// <summary>
    /// Export names keyed by the RVA in the export address table.
    /// </summary>
    public Dictionary<int, string> ExportNamesByRva { get; } = new();

    /// <summary>
    /// A section name of the form "/123" is an offset into the COFF string
    /// table, which is located after the COFF symbol table.
    /// </summary>
    private void ResolveLongSectionNames()
    {
        long stringTable = (long)PEHeader.PointerToSymbolTable.Value + (long)PEHeader.NumberOfSymbols.Value * 18;
        if (PEHeader.PointerToSymbolTable.Value <= 0 || stringTable + 4 > Buffer.Length)
        {
            return;
        }

        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            string name = sectionHeader.Name.Text;
            if (name.Length < 2 || name[0] != '/' || !int.TryParse(name.Substring(1), out int offset))
            {
                continue;
            }

            string longName = stringTable + offset < Buffer.Length ? ReadZeroTerminatedAscii((int)stringTable + offset) : null;
            if (!string.IsNullOrEmpty(longName))
            {
                sectionHeader.Name.Text = longName;
                sectionHeader.Text = $"{longName} section header";
            }
        }
    }

    /// <summary>
    /// Data directory 7 is reserved today. Old images use it for a copyright
    /// or description string.
    /// </summary>
    private void AddArchitectureDirectory()
    {
        var directory = OptionalHeader.DataDirectories.Architecture;
        int size = directory.Size.Value;
        int offset = directory.RVA.Value > 0 ? ResolveVirtualAddress(directory.RVA.Value) : 0;
        if (offset <= 0 || size <= 0 || !IsRangeAvailable(offset, size))
        {
            return;
        }

        var data = new Utf8String { Start = offset, Length = size };
        Add(data);
        string text = ReadZeroTerminatedAscii(offset, size);
        data.Text = text != null ? $"Copyright string: {text}" : "Architecture-specific data";
    }

    private int[] relocationOffsets = Array.Empty<int>();

    public bool HasRelocations => relocationOffsets.Length > 0;

    public bool IsRelocated(int offset) => Array.BinarySearch(relocationOffsets, offset) >= 0;

    /// <summary>
    /// File offsets of every pointer-sized slot the base relocation table
    /// lists. A relocated slot holds a virtual address.
    /// </summary>
    private void ReadRelocations()
    {
        var directory = OptionalHeader.DataDirectories.BaseRelocationTable;
        int size = directory.Size.Value;
        int start = ResolveDataDirectory(directory);
        if (start <= 0 || size < 8 || (long)start + size > Buffer.Length)
        {
            return;
        }

        const int HighLow = 3;
        const int Dir64 = 10;

        var bytes = Buffer.ReadBytes(start, size);
        var list = new List<int>();
        int position = 0;
        while (position + 8 <= size)
        {
            int pageRva = BitConverter.ToInt32(bytes, position);
            int blockSize = BitConverter.ToInt32(bytes, position + 4);
            if (blockSize < 8 || position + blockSize > size)
            {
                break;
            }

            int pageOffset = ResolveVirtualAddress(pageRva);
            var pageSection = GetSectionAtVirtualAddress(pageRva);
            if (pageOffset > 0 && pageSection != null)
            {
                int sectionRawEnd = pageSection.PointerToRawData.Value + pageSection.SizeOfRawData.Value;
                for (int entry = position + 8; entry + 2 <= position + blockSize; entry += 2)
                {
                    int value = BitConverter.ToUInt16(bytes, entry);
                    int type = value >> 12;
                    if (type != HighLow && type != Dir64)
                    {
                        continue;
                    }

                    int offset = pageOffset + (value & 0xFFF);
                    if (offset + (type == Dir64 ? 8 : 4) <= sectionRawEnd)
                    {
                        list.Add(offset);
                    }
                }
            }

            position += blockSize;
        }

        list.Sort();
        relocationOffsets = list.ToArray();
    }

    private int FirstRelocationAtOrAfter(int offset)
    {
        int index = Array.BinarySearch(relocationOffsets, offset);
        return index >= 0 ? index : ~index;
    }

    private bool IsCodePadding(Span span)
    {
        var bytes = Buffer.ReadBytes(span.Start, span.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b != 0xCC && b != 0x90 && b != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Labels every byte range that no parser claimed. Inside sections the
    /// bytes are split into relocated pointers, strings, zero padding and
    /// unknown data; elsewhere a range is either padding or unknown.
    /// </summary>
    private void ClassifyUncoveredSpans()
    {
        var gaps = new List<(Node container, int start, int length)>();
        CollectGaps(this, gaps);

        foreach (var (container, start, length) in gaps)
        {
            if (container is Section || container is Region)
            {
                ClassifySectionGap(container, start, length);
            }
            else
            {
                var span = new Span(start, length);
                Node node = Buffer.IsZeroFilled(span) || IsPaddingFilled(span)
                    ? new Padding()
                    : new Unknown();
                node.Start = start;
                node.Length = length;
                container.Add(node);
            }
        }
    }

    private static void CollectGaps(Node node, List<(Node container, int start, int length)> gaps)
    {
        if (!node.HasChildren)
        {
            if (node is Section && node.Length > 0)
            {
                gaps.Add((node, node.Start, node.Length));
            }

            return;
        }

        int index = node.Start;
        var children = node.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Start > index)
            {
                gaps.Add((node, index, child.Start - index));
            }

            CollectGaps(child, gaps);
            index = child.End;
        }

        if (index < node.End)
        {
            gaps.Add((node, index, node.End - index));
        }
    }

    private void ClassifySectionGap(Node container, int start, int length)
    {
        int end = start + length;
        int position = start;
        int pointerSize = PointerSize;
        int relocationIndex = FirstRelocationAtOrAfter(start);

        while (relocationIndex < relocationOffsets.Length)
        {
            int runStart = relocationOffsets[relocationIndex];
            if (runStart + pointerSize > end)
            {
                break;
            }

            if (runStart < position)
            {
                relocationIndex++;
                continue;
            }

            int count = 1;
            while (relocationIndex + count < relocationOffsets.Length &&
                relocationOffsets[relocationIndex + count] == runStart + count * pointerSize &&
                runStart + (count + 1) * pointerSize <= end)
            {
                count++;
            }

            ClassifyRawBytes(container, position, runStart);
            container.Add(new PointerTable
            {
                Start = runStart,
                Length = count * pointerSize,
                PointerSize = pointerSize
            });
            position = runStart + count * pointerSize;
            relocationIndex += count;
        }

        ClassifyRawBytes(container, position, end);
    }

    private void ClassifyRawBytes(Node container, int start, int end)
    {
        int length = end - start;
        if (length <= 0)
        {
            return;
        }

        var bytes = Buffer.ReadBytes(start, length);
        if (IsPaddingPattern(bytes))
        {
            container.Add(new Padding { Start = start, Length = length });
            return;
        }

        bool guessPointers = !HasRelocations;
        int pointerSize = PointerSize;
        ulong lowestAddress = ImageBase + (uint)OptionalHeader.WindowsFields.SectionAlignment.Value;
        ulong highestAddress = ImageBase + SizeOfImage;

        int unknownStart = 0;
        int i = 0;

        void Flush(int upTo)
        {
            if (upTo > unknownStart)
            {
                container.Add(new Unknown { Start = start + unknownStart, Length = upTo - unknownStart });
            }
        }

        while (i < length)
        {
            byte b = bytes[i];
            if (b == 0)
            {
                int j = i;
                while (j < length && bytes[j] == 0)
                {
                    j++;
                }

                int run = j - i;
                if (run >= 4 || i == unknownStart || j == length)
                {
                    Flush(i);
                    container.Add(new Padding { Start = start + i, Length = run });
                    unknownStart = j;
                }

                i = j;
                continue;
            }

            if (guessPointers && ((start + i) % pointerSize) == 0 && i + pointerSize <= length)
            {
                int count = 0;
                while (i + (count + 1) * pointerSize <= length)
                {
                    ulong value = pointerSize == 8
                        ? BitConverter.ToUInt64(bytes, i + count * pointerSize)
                        : BitConverter.ToUInt32(bytes, i + count * pointerSize);
                    if (value < lowestAddress || value >= highestAddress)
                    {
                        break;
                    }

                    count++;
                }

                if (count > 0)
                {
                    Flush(i);
                    container.Add(new PointerTable
                    {
                        Start = start + i,
                        Length = count * pointerSize,
                        PointerSize = pointerSize
                    });
                    i += count * pointerSize;
                    unknownStart = i;
                    continue;
                }
            }

            int stringLength = ((start + i) & 1) == 0 ? MeasureUtf16String(bytes, i) : 0;
            bool isUtf16 = stringLength > 0;
            if (!isUtf16)
            {
                stringLength = MeasureAsciiString(bytes, i);
            }

            if (stringLength > 0)
            {
                Flush(i);
                container.Add(new StringLiteral
                {
                    Start = start + i,
                    Length = stringLength,
                    IsUtf16 = isUtf16
                });
                i += stringLength;
                unknownStart = i;
                continue;
            }

            i++;
        }

        Flush(length);
    }

    private static bool IsPaddingPattern(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            if (Array.IndexOf(PaddingPattern, bytes[i]) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTextByte(byte b)
    {
        return (b >= 0x20 && b <= 0x7E) || b == 0x09 || b == 0x0A || b == 0x0D;
    }

    private const int MinimumStringLength = 4;

    private static int MeasureAsciiString(byte[] bytes, int start)
    {
        int i = start;
        while (i < bytes.Length && IsTextByte(bytes[i]))
        {
            i++;
        }

        int characters = i - start;
        if (characters < MinimumStringLength || i >= bytes.Length || bytes[i] != 0)
        {
            return 0;
        }

        return characters + 1;
    }

    private static int MeasureUtf16String(byte[] bytes, int start)
    {
        int i = start;
        while (i + 1 < bytes.Length && bytes[i + 1] == 0 && IsTextByte(bytes[i]))
        {
            i += 2;
        }

        int characters = (i - start) / 2;
        if (characters < MinimumStringLength || i + 1 >= bytes.Length || bytes[i] != 0 || bytes[i + 1] != 0)
        {
            return 0;
        }

        return (characters + 1) * 2;
    }
}

public class PointerTable : Node
{
    public int PointerSize { get; set; }

    public override void Parse()
    {
        int count = Length / PointerSize;
        for (int i = 0; i < count; i++)
        {
            BytesNode pointer = PointerSize == 8 ? AddEightBytes() : AddFourBytes();
            ulong value = PointerSize == 8 ? Buffer.ReadUInt64(pointer.Start) : Buffer.ReadUInt32(pointer.Start);
            pointer.Text = $"Pointer 0x{value:X}";
        }

        Text = count == 1 ? "Pointer" : $"Pointers ({count})";
    }
}

public class StringLiteral : Node
{
    public bool IsUtf16 { get; set; }

    public override void Parse()
    {
        var bytes = Buffer.ReadBytes(Start, Length);
        string value = IsUtf16
            ? Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2)
            : Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
        Value = value;
        Text = IsUtf16 ? $"L\"{value}\"" : $"\"{value}\"";
    }

    public string Value { get; set; }
}
