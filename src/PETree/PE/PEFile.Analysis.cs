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
    /// The file offsets of the relocated slots, in ascending order.
    /// </summary>
    public IReadOnlyList<int> RelocationOffsets => relocationOffsets;

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
    protected void ClassifyUncoveredSpans()
    {
        var gaps = new List<(Node container, int start, int length)>();
        CollectGaps(this, gaps);

        // The new nodes of a container are collected first and merged into
        // its children once: a section can get hundreds of thousands of them
        var newNodes = new Dictionary<Node, List<Node>>();
        foreach (var (container, start, length) in gaps)
        {
            if (!newNodes.TryGetValue(container, out var output))
            {
                output = new List<Node>();
                newNodes[container] = output;
            }

            if (container is Region { GapText: not null } opaque)
            {
                output.Add(new Region { Start = start, Length = length, Text = opaque.GapText });
            }
            else if (container is Section || container is Region)
            {
                ClassifySectionGap(output, start, length);
            }
            else
            {
                var span = new Span(start, length);
                Node node = Buffer.IsZeroFilled(span) || IsPaddingFilled(span)
                    ? new Padding()
                    : container is Dotnet.IL && IsStrippedIL(span)
                        ? new StrippedMethodBodies()
                        : new Unknown();
                node.Start = start;
                node.Length = length;
                output.Add(node);
            }
        }

        foreach (var pair in newNodes)
        {
            pair.Key.AddBetweenChildren(pair.Value);
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

    /// <summary>
    /// The IL stripper of an ahead-of-time build fills a method body with
    /// zeros and leaves the first bytes of its header. A range of the IL
    /// area that no method claims and that is zero except for such
    /// remnants is what the stripper left.
    /// </summary>
    private bool IsStrippedIL(Span span)
    {
        const int MinimumLength = 12;
        if (span.Length < MinimumLength)
        {
            return false;
        }

        var bytes = Buffer.ReadBytes(span.Start, span.Length);
        int nonZero = 0;
        foreach (byte b in bytes)
        {
            if (b != 0)
            {
                nonZero++;
            }
        }

        return nonZero * 4 <= bytes.Length;
    }

    /// <summary>
    /// Names of pointer tables by file offset, for tables whose start is
    /// known from a reference and whose bytes are labelled with the gaps.
    /// </summary>
    public Dictionary<int, string> PointerTableNames { get; } = new();

    private void ClassifySectionGap(List<Node> output, int start, int length)
    {
        int end = start + length;
        int position = start;
        int pointerSize = PointerSize;
        int relocationIndex = FirstRelocationAtOrAfter(start);
        int rva = SectionTable != null ? OffsetToRva(start) : 0;
        bool isCode = rva != 0 && IsCodeAddress(rva);

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

            // A run ends where a table with a known name starts
            int count = 1;
            while (relocationIndex + count < relocationOffsets.Length &&
                relocationOffsets[relocationIndex + count] == runStart + count * pointerSize &&
                runStart + (count + 1) * pointerSize <= end &&
                !PointerTableNames.ContainsKey(runStart + count * pointerSize))
            {
                count++;
            }

            ClassifyRawBytes(output, position, runStart, isCode);
            if (PointerTableNames.TryGetValue(runStart, out string tableName))
            {
                output.Add(new RecordTable
                {
                    Start = runStart,
                    Length = count * pointerSize,
                    Count = count,
                    Fields = [("Pointer", pointerSize)],
                    Text = $"{tableName} ({count} entries)"
                });
            }
            else
            {
                output.Add(new PointerTable
                {
                    Start = runStart,
                    Length = count * pointerSize,
                    PointerSize = pointerSize
                });
            }
            position = runStart + count * pointerSize;
            relocationIndex += count;
        }

        ClassifyRawBytes(output, position, end, isCode);
    }

    private void ClassifyRawBytes(List<Node> output, int start, int end, bool isCode)
    {
        int length = end - start;
        if (length <= 0)
        {
            return;
        }

        var bytes = Buffer.ReadBytes(start, length);
        if (IsPaddingPattern(bytes) || (isCode && IsCodePadding(bytes, start)))
        {
            output.Add(new Padding { Start = start, Length = length });
            return;
        }

        // Breakpoint bytes fill the space between functions and their data
        bool hasBreakpointPadding = isCode && (Machine == MachineAmd64 || Machine == MachineI386);

        // An image without PE headers (Webcil) has no image base to compare with
        bool guessPointers = !HasRelocations && OptionalHeader != null;
        int pointerSize = PointerSize;
        ulong lowestAddress = guessPointers ? ImageBase + (uint)OptionalHeader.WindowsFields.SectionAlignment.Value : 0;
        ulong highestAddress = guessPointers ? ImageBase + SizeOfImage : 0;

        int unknownStart = 0;
        int i = 0;

        void Flush(int upTo)
        {
            if (upTo > unknownStart)
            {
                output.Add(new Unknown { Start = start + unknownStart, Length = upTo - unknownStart });
            }
        }

        // A table of constants at the current place
        bool AddConstantTable()
        {
            if (((start + i) & 3) != 0)
            {
                return false;
            }

            var table = ReadConstantTable(bytes, i, start);
            if (table == null)
            {
                return false;
            }

            Flush(i);

            // The first entry of a checksum table is zero and was taken for padding
            if (table is ChecksumTable && output.Count > 0 && output[output.Count - 1] is Padding padding &&
                padding.End == table.Start && padding.Length >= 4)
            {
                if (padding.Length == 4)
                {
                    output.RemoveAt(output.Count - 1);
                }
                else
                {
                    padding.Length -= 4;
                }

                table.Start -= 4;
                table.Length += 4;
            }

            output.Add(table);
            i = table.End - start;
            unknownStart = i;
            return true;
        }

        while (i < length)
        {
            byte b = bytes[i];
            if (b == 0)
            {
                // A value of a table can start with zero bytes
                if (AddConstantTable())
                {
                    continue;
                }

                int j = i;
                while (j < length && bytes[j] == 0)
                {
                    j++;
                }

                int run = j - i;
                if (run >= 4 || i == unknownStart || j == length)
                {
                    Flush(i);
                    output.Add(new Padding { Start = start + i, Length = run });
                    unknownStart = j;
                }

                i = j;
                continue;
            }

            if (b == 0xCC && hasBreakpointPadding)
            {
                int j = i;
                while (j < length && bytes[j] == 0xCC)
                {
                    j++;
                }

                int run = j - i;
                if (run >= 4 || i == 0 || j == length)
                {
                    Flush(i);
                    output.Add(new Padding { Start = start + i, Length = run });
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
                    output.Add(new PointerTable
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
                output.Add(new StringLiteral
                {
                    Start = start + i,
                    Length = stringLength,
                    IsUtf16 = isUtf16
                });
                i += stringLength;
                unknownStart = i;
                continue;
            }

            if (AddConstantTable())
            {
                continue;
            }

            i++;
        }

        Flush(length);
    }

    /// <summary>
    /// True when a range of a code section has nothing but the bytes that
    /// compilers put between functions: breakpoints and instructions that
    /// do nothing.
    /// </summary>
    private bool IsCodePadding(byte[] bytes, int start)
    {
        if (Machine == MachineArm64)
        {
            const uint Nop = 0xD503201F;
            if ((start & 3) != 0 || (bytes.Length & 3) != 0)
            {
                return false;
            }

            for (int i = 0; i < bytes.Length; i += 4)
            {
                // nop, brk with any number, or a zero word
                uint word = BitConverter.ToUInt32(bytes, i);
                if (word != 0 && word != Nop && (word & 0xFFE0001F) != 0xD4200000)
                {
                    return false;
                }
            }

            return true;
        }

        if (Machine != MachineAmd64 && Machine != MachineI386)
        {
            return false;
        }

        int position = 0;
        while (position < bytes.Length)
        {
            if (bytes[position] == 0xCC || bytes[position] == 0)
            {
                position++;
                continue;
            }

            int length = MeasureNop(bytes, position);
            if (length == 0)
            {
                return false;
            }

            position += length;
        }

        return true;
    }

    /// <summary>
    /// The length of an x86 instruction that does nothing and is used to
    /// align code, or 0: nop with prefixes, the long nop, and the forms of
    /// lea, mov and xchg with the same register on both sides.
    /// </summary>
    private static int MeasureNop(byte[] bytes, int start)
    {
        int end = bytes.Length;
        int p = start;
        while (p < end && bytes[p] == 0x66)
        {
            p++;
        }

        if (p < end && bytes[p] == 0x90)
        {
            return p + 1 - start;
        }

        if (p + 2 < end && bytes[p] == 0x0F && bytes[p + 1] == 0x1F)
        {
            // The long nop, by the addressing mode of its operand
            int length = bytes[p + 2] switch
            {
                0x00 => 3,
                0x40 => 4,
                0x44 => 5,
                0x80 => 7,
                0x84 => 8,
                _ => 0
            };
            return length > 0 && p + length <= end ? p + length - start : 0;
        }

        if (p != start)
        {
            return 0;
        }

        byte first = bytes[p];
        byte second = p + 1 < end ? bytes[p + 1] : (byte)0;
        if (p + 1 < end && ((first == 0x8B && (second == 0xFF || second == 0xC0)) || (first == 0x87 && second == 0xDB)))
        {
            return 2;
        }

        if (first != 0x8D)
        {
            return 0;
        }

        // lea of a register from itself, with a displacement of zero
        if (p + 2 < end && (second == 0x49 || second == 0x40 || second == 0x76) && bytes[p + 2] == 0)
        {
            return 3;
        }

        if (p + 3 < end && second == 0x64 && bytes[p + 2] == 0x24 && bytes[p + 3] == 0)
        {
            return 4;
        }

        if (p + 5 < end && (second == 0x9B || second == 0x80 || second == 0xB6 || second == 0xBF) && BitConverter.ToInt32(bytes, p + 2) == 0)
        {
            return 6;
        }

        if (p + 6 < end && (second == 0xA4 || second == 0xB4 || second == 0xBC) &&
            (bytes[p + 2] == 0x24 || bytes[p + 2] == 0x26 || bytes[p + 2] == 0x27) && BitConverter.ToInt32(bytes, p + 3) == 0)
        {
            return 7;
        }

        return 0;
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

    public static int MeasureAsciiString(byte[] bytes, int start)
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

    public static int MeasureUtf16String(byte[] bytes, int start)
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

/// <summary>
/// Method bodies that the IL stripper filled with zeros.
/// </summary>
public class StrippedMethodBodies : Padding
{
    public StrippedMethodBodies()
    {
        Text = "Method bodies removed by the IL stripper";
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
