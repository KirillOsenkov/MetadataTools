using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GuiLabs.FileFormat.PE;

namespace GuiLabs.FileFormat.Wasm;

/// <summary>
/// A WebAssembly module: an 8-byte header and a sequence of sections. Each
/// section is an id byte, a size and a payload whose layout depends on the id.
/// </summary>
public partial class WasmFile : Node
{
    internal byte[] Bytes;

    /// <summary>
    /// Initial values of the globals that are integer constants. In a module
    /// that Emscripten links, the first one is the stack pointer.
    /// </summary>
    public List<long> GlobalValues { get; } = new();

    /// <summary>
    /// Initial number of elements of the largest table. A function pointer
    /// in the data is an index into the function table.
    /// </summary>
    public long FunctionTableSize { get; set; }

    /// <summary>
    /// Number of function bodies with an instruction that the instruction
    /// reader does not know.
    /// </summary>
    public int UndecodedFunctionCount { get; set; }

    /// <summary>
    /// Function index by position in the function table, from the element
    /// segments. A function pointer in the data is such a position.
    /// </summary>
    public Dictionary<long, int> TableFunctions { get; } = new();

    /// <summary>
    /// The functions that have a body in the module, in the order of the code section.
    /// </summary>
    public List<WasmFunction> Functions { get; } = new();

    /// <summary>
    /// Function names by function index, from the import section, the
    /// export section and the "name" custom section.
    /// </summary>
    public Dictionary<int, string> FunctionNames { get; } = new();

    public int ImportedFunctionCount { get; set; }
    public List<WasmSection> Sections { get; } = new();
    public List<WasmDataSegment> DataSegments { get; } = new();
    public WebcilImage Webcil { get; private set; }

    public static bool IsWasmFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.ReadByte() == 0 && stream.ReadByte() == 'a' && stream.ReadByte() == 's' && stream.ReadByte() == 'm';
    }

    public static WasmFile ReadFromFile(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var file = new WasmFile { Buffer = new StreamBuffer(new MemoryStream(bytes)) };
        file.Parse();
        return file;
    }

    public override void Parse()
    {
        Bytes = Buffer.ReadBytes(0, (int)Buffer.Length);
        if (Bytes.Length < 8 || Bytes[0] != 0 || Bytes[1] != 'a' || Bytes[2] != 's' || Bytes[3] != 'm')
        {
            Text = "Not a WebAssembly module";
            if (Bytes.Length > 0)
            {
                Add(new Unknown { Length = Bytes.Length, Text = "Not a WebAssembly module" });
            }

            return;
        }

        Add(new Record { Fields = [("Magic number", 4), ("Version", 4)], Text = "WebAssembly header" });

        // The section boundaries first: the names at the end of the file are
        // needed to label the functions in the middle
        var boundaries = new List<(int id, int start, int payload, int size)>();
        int position = 8;
        while (position < Bytes.Length)
        {
            int start = position;
            int id = Bytes[position++];
            if (!WasmReader.TryReadLeb(Bytes, ref position, out ulong size) || position + (long)size > Bytes.Length)
            {
                Add(new Unknown { Start = start, Length = Bytes.Length - start, Text = "Truncated section" });
                break;
            }

            boundaries.Add((id, start, position, (int)size));
            position += (int)size;
        }

        foreach (var (id, _, payload, size) in boundaries)
        {
            if (id == 0)
            {
                ReadNames(payload, size);
            }
        }

        foreach (var (id, start, payload, size) in boundaries)
        {
            var section = new WasmSection
            {
                Start = start,
                Length = payload + size - start,
                Id = id,
                PayloadStart = payload,
                File = this
            };
            Add(section);
            Sections.Add(section);
        }

        if (!TryAddWebcil())
        {
            ClassifyDataSegments();
        }

        Text = Webcil != null ? "WebAssembly module with a Webcil assembly" : "WebAssembly module";
    }

    public string DescribeFunction(int index)
    {
        return FunctionNames.TryGetValue(index, out string name) ? $"Function {index}: {name}" : $"Function {index}";
    }

    /// <summary>
    /// Names of the data segments of an object file, from its "linking" section.
    /// </summary>
    public List<string> SegmentNames { get; } = new();

    /// <summary>
    /// The data symbols of an object file: each one names a byte range of a data segment.
    /// </summary>
    public List<(string name, int segment, int offset, int size)> DataSymbols { get; } = new();

    /// <summary>
    /// For each symbol of an object file, in the order of the symbol table:
    /// the function index when the symbol is a function, and -1 otherwise.
    /// A relocation refers to a symbol by its position in this list.
    /// </summary>
    public List<int> SymbolFunctions { get; } = new();

    /// <summary>
    /// The function pointers in the data of an object file, from its
    /// relocation section: file offset of the 4-byte value, and the position
    /// of the function symbol in the symbol table. The value in the file is
    /// a placeholder; the linker writes the function table index there.
    /// </summary>
    public Dictionary<int, int> FunctionPointerRelocations { get; } = new();

    /// <summary>
    /// The "linking" custom section of an object file: the names of the data
    /// segments and the symbol table, which names functions and data ranges.
    /// </summary>
    private void ReadLinkingSection(int position, int end)
    {
        const int SegmentInfo = 5;
        const int SymbolTable = 8;
        const int SymbolFunction = 0;
        const int SymbolData = 1;
        const int SymbolSection = 3;
        const int FlagUndefined = 0x10;
        const int FlagExplicitName = 0x40;

        WasmReader.ReadLeb(Bytes, ref position);
        while (position < end)
        {
            int id = Bytes[position++];
            int subsectionSize = (int)WasmReader.ReadLeb(Bytes, ref position);
            int subsectionEnd = position + subsectionSize;
            if (id == SegmentInfo)
            {
                uint count = (uint)WasmReader.ReadLeb(Bytes, ref position);
                for (uint i = 0; i < count && position < subsectionEnd; i++)
                {
                    SegmentNames.Add(WasmReader.ReadName(Bytes, ref position));
                    WasmReader.ReadLeb(Bytes, ref position);
                    WasmReader.ReadLeb(Bytes, ref position);
                }
            }
            else if (id == SymbolTable)
            {
                uint count = (uint)WasmReader.ReadLeb(Bytes, ref position);
                for (uint i = 0; i < count && position < subsectionEnd; i++)
                {
                    int kind = Bytes[position++];
                    uint flags = (uint)WasmReader.ReadLeb(Bytes, ref position);
                    bool isDefined = (flags & FlagUndefined) == 0;
                    SymbolFunctions.Add(-1);
                    if (kind == SymbolData)
                    {
                        string name = WasmReader.ReadName(Bytes, ref position);
                        if (isDefined)
                        {
                            int segment = (int)WasmReader.ReadLeb(Bytes, ref position);
                            int offset = (int)WasmReader.ReadLeb(Bytes, ref position);
                            int symbolSize = (int)WasmReader.ReadLeb(Bytes, ref position);
                            DataSymbols.Add((name, segment, offset, symbolSize));
                        }
                    }
                    else if (kind == SymbolSection)
                    {
                        WasmReader.ReadLeb(Bytes, ref position);
                    }
                    else
                    {
                        int index = (int)WasmReader.ReadLeb(Bytes, ref position);
                        if (kind == SymbolFunction)
                        {
                            SymbolFunctions[SymbolFunctions.Count - 1] = index;
                        }

                        if (isDefined || (flags & FlagExplicitName) != 0)
                        {
                            string name = WasmReader.ReadName(Bytes, ref position);
                            if (kind == SymbolFunction)
                            {
                                FunctionNames[index] = name;
                            }
                        }
                    }
                }
            }

            position = subsectionEnd;
        }
    }

    /// <summary>
    /// The "name" custom section: subsection 1 maps function indices to names.
    /// </summary>
    private void ReadNames(int payload, int size)
    {
        const int FunctionNamesSubsection = 1;
        try
        {
            int position = payload;
            int end = payload + size;
            string sectionName = WasmReader.ReadName(Bytes, ref position);
            if (sectionName == "linking")
            {
                ReadLinkingSection(position, end);
                return;
            }

            if (sectionName != "name")
            {
                return;
            }

            while (position < end)
            {
                int id = Bytes[position++];
                int subsectionSize = (int)WasmReader.ReadLeb(Bytes, ref position);
                int subsectionEnd = position + subsectionSize;
                if (id == FunctionNamesSubsection)
                {
                    uint count = (uint)WasmReader.ReadLeb(Bytes, ref position);
                    for (uint i = 0; i < count && position < subsectionEnd; i++)
                    {
                        int index = (int)WasmReader.ReadLeb(Bytes, ref position);
                        FunctionNames[index] = WasmReader.ReadName(Bytes, ref position);
                    }
                }

                position = subsectionEnd;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is InvalidDataException)
        {
        }
    }

    /// <summary>
    /// A Webcil module has two passive data segments: the size of the
    /// payload, and the payload, which starts with "WbIL".
    /// </summary>
    private bool TryAddWebcil()
    {
        if (DataSegments.Count != 2 || DataSegments[0].Address >= 0 || DataSegments[1].Address >= 0)
        {
            return false;
        }

        var sizeSegment = DataSegments[0];
        var payload = DataSegments[1];
        if (payload.ContentLength < 28 || BitConverter.ToUInt32(Bytes, payload.ContentStart) != WebcilImage.Signature)
        {
            return false;
        }

        if (sizeSegment.ContentLength >= 4)
        {
            sizeSegment.Add(new FourBytes { Start = sizeSegment.ContentStart, Text = "Webcil payload size" });
            if (sizeSegment.ContentLength >= 8)
            {
                sizeSegment.Add(new FourBytes { Start = sizeSegment.ContentStart + 4, Text = "Table size" });
            }

            int rest = sizeSegment.ContentStart + sizeSegment.ContentLength - sizeSegment.LastChildEnd;
            if (rest > 0)
            {
                sizeSegment.Add(new Padding { Start = sizeSegment.LastChildEnd, Length = rest });
            }
        }

        var image = new WebcilImage(Buffer) { Start = payload.ContentStart, Length = payload.ContentLength };
        payload.Add(image);
        Webcil = image;
        return true;
    }

    /// <summary>
    /// Splits the bytes of the data segments into zero padding, strings,
    /// values that are addresses of other data, and unknown bytes.
    /// </summary>
    private void ClassifyDataSegments()
    {
        long lowestAddress = long.MaxValue;
        long highestAddress = 0;
        foreach (var segment in DataSegments)
        {
            if (segment.Address >= 0)
            {
                lowestAddress = Math.Min(lowestAddress, segment.Address);
                highestAddress = Math.Max(highestAddress, segment.Address + segment.ContentLength);
            }
        }

        // In an object file the segment addresses are not final, so values
        // are not compared with them
        bool isObjectFile = SegmentNames.Count > 0 || DataSymbols.Count > 0;
        var symbolsBySegment = DataSymbols
            .Where(s => s.size > 0)
            .GroupBy(s => s.segment)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.offset).ToList());

        // Zero-initialized data and the stack are located after the data
        // that is in the file, up to the initial value of the stack pointer
        long addressLimit = highestAddress;
        if (!isObjectFile)
        {
            foreach (long value in GlobalValues)
            {
                addressLimit = Math.Max(addressLimit, value);
            }
        }

        var monoAotData = ReadMonoAotData(Math.Max(addressLimit, highestAddress));

        foreach (var segment in DataSegments)
        {
            if (segment.ContentLength <= 0)
            {
                continue;
            }

            monoAotData.TryGetValue(segment.Index, out var monoAotNodes);

            // The ranges with a known meaning: the data symbols of an object
            // file, or else the Mono AOT tables
            var claimed = new List<Node>();
            int position = segment.ContentStart;
            int end = segment.ContentStart + segment.ContentLength;
            if (symbolsBySegment.TryGetValue(segment.Index, out var symbols))
            {
                foreach (var symbol in symbols)
                {
                    int symbolStart = segment.ContentStart + symbol.offset;
                    if (symbolStart < position || symbolStart + (long)symbol.size > end)
                    {
                        continue;
                    }

                    var region = new Region { Start = symbolStart, Length = symbol.size, Text = $"Data symbol {symbol.name}" };
                    claimed.Add(region);
                    position = symbolStart + symbol.size;
                }

                // A decoded table is shown inside the symbol that it is the contents of
                if (monoAotNodes != null)
                {
                    foreach (var node in monoAotNodes)
                    {
                        var region = claimed.Find(r => r.Start == node.Start && r.Length == node.Length);
                        if (region != null && !region.HasChildren)
                        {
                            node.Parent = region;
                            region.Children.Add(node);
                        }
                    }
                }
            }
            else if (monoAotNodes != null)
            {
                monoAotNodes.Sort((left, right) => left.Start.CompareTo(right.Start));
                claimed.AddRange(monoAotNodes);
            }

            var output = new List<Node>();
            position = segment.ContentStart;
            foreach (var node in claimed)
            {
                if (node.Start > position)
                {
                    ClassifyData(position, node.Start - position, AddressOf(position), lowestAddress, addressLimit, output);
                }

                output.Add(node);
                position = node.End;
            }

            if (position < end)
            {
                ClassifyData(position, end - position, AddressOf(position), lowestAddress, addressLimit, output);
            }

            segment.AddBetweenChildren(output);

            long AddressOf(int fileOffset) => isObjectFile || segment.Address < 0 ? -1 : segment.Address + fileOffset - segment.ContentStart;
        }
    }

    private void ClassifyData(int start, int length, long address, long lowestAddress, long highestAddress, List<Node> output)
    {
        var bytes = Bytes;
        int end = start + length;
        int i = start;
        int unknownStart = start;

        void Flush(int upTo)
        {
            if (upTo > unknownStart)
            {
                output.Add(new Unknown { Start = unknownStart, Length = upTo - unknownStart });
            }
        }

        while (i < end)
        {
            if (bytes[i] == 0)
            {
                int j = i;
                while (j < end && bytes[j] == 0)
                {
                    j++;
                }

                int run = j - i;
                if (run >= 4 || i == unknownStart || j == end)
                {
                    Flush(i);
                    output.Add(new Padding { Start = i, Length = run });
                    unknownStart = j;
                }

                i = j;
                continue;
            }

            // A value at an aligned address that is the address of data. One
            // such value alone has to be aligned itself to count.
            if (address >= 0 && ((address + i - start) & 3) == 0 && i + 4 <= end)
            {
                int count = 0;
                while (i + (count + 1) * 4 <= end)
                {
                    uint value = BitConverter.ToUInt32(bytes, i + count * 4);
                    if (value < lowestAddress || value >= highestAddress)
                    {
                        break;
                    }

                    count++;
                }

                if (count >= 2 || (count == 1 && (BitConverter.ToUInt32(bytes, i) & 3) == 0))
                {
                    Flush(i);
                    output.Add(new PointerTable { Start = i, Length = count * 4, PointerSize = 4 });
                    i += count * 4;
                    unknownStart = i;
                    continue;
                }
            }

            bool isEvenAddress = address < 0 ? ((i - start) & 1) == 0 : ((address + i - start) & 1) == 0;
            int stringLength = isEvenAddress ? PEFile.MeasureUtf16String(bytes, i) : 0;
            bool isUtf16 = stringLength > 0 && i + stringLength <= end;
            if (!isUtf16)
            {
                stringLength = PEFile.MeasureAsciiString(bytes, i);
            }

            if (stringLength > 0 && i + stringLength <= end)
            {
                Flush(i);
                output.Add(new StringLiteral { Start = i, Length = stringLength, IsUtf16 = isUtf16 });
                i += stringLength;
                unknownStart = i;
                continue;
            }

            // Text that is stored with its length and not with a terminator,
            // such as an embedded source file or a word list
            int textLength = MeasureText(bytes, i, end);
            if (textLength >= MinimumTextLength)
            {
                Flush(i);
                output.Add(new Node { Start = i, Length = textLength, Text = $"Text ({textLength:N0} characters)" });
                i += textLength;
                unknownStart = i;
                continue;
            }

            i++;
        }

        Flush(end);
    }

    private const int MinimumTextLength = 32;

    /// <summary>
    /// Number of bytes from the start that are printable ASCII characters,
    /// tabs or line breaks.
    /// </summary>
    private static int MeasureText(byte[] bytes, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            byte b = bytes[i];
            if ((b < 0x20 || b > 0x7E) && b != '\t' && b != '\n' && b != '\r')
            {
                break;
            }

            i++;
        }

        return i - start;
    }
}

public static class WasmReader
{
    public static bool TryReadLeb(byte[] bytes, ref int position, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (position < bytes.Length && shift < 70)
        {
            byte b = bytes[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    public static ulong ReadLeb(byte[] bytes, ref int position)
    {
        if (!TryReadLeb(bytes, ref position, out ulong value))
        {
            throw new InvalidDataException("Invalid LEB128 number");
        }

        return value;
    }

    public static long ReadSignedLeb(byte[] bytes, ref int position)
    {
        long value = 0;
        int shift = 0;
        while (true)
        {
            if (position >= bytes.Length || shift >= 70)
            {
                throw new InvalidDataException("Invalid LEB128 number");
            }

            byte b = bytes[position++];
            value |= (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (shift < 64 && (b & 0x40) != 0)
                {
                    value |= -1L << shift;
                }

                return value;
            }
        }
    }

    public static string ReadName(byte[] bytes, ref int position)
    {
        int length = (int)ReadLeb(bytes, ref position);
        if (length < 0 || position + length > bytes.Length)
        {
            throw new InvalidDataException("Invalid name length");
        }

        string name = Encoding.UTF8.GetString(bytes, position, length);
        position += length;
        return name;
    }
}

public class WasmSection : Section
{
    public int Id { get; set; }
    public int PayloadStart { get; set; }
    public WasmFile File { get; set; }
    public string Name { get; set; }

    private byte[] bytes;
    private int position;
    private ulong lastLimitMinimum;

    private static readonly string[] sectionNames =
    [
        "custom", "type", "import", "function", "table", "memory", "global", "export",
        "start", "element", "code", "data", "data count", "tag"
    ];

    private static readonly string[] externalKinds = ["function", "table", "memory", "global", "tag"];

    public override void Parse()
    {
        bytes = File.Bytes;
        Name = Id < sectionNames.Length ? sectionNames[Id] : $"unknown ({Id})";
        Add(new OneByte { Start = Start, Text = $"Section id: {Id} ({Name})" });
        Add(new Node { Start = Start + 1, Length = PayloadStart - Start - 1, Text = $"Section size: {End - PayloadStart}" });

        position = PayloadStart;
        Text = $"{Name} section";
        try
        {
            ParsePayload();
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is InvalidDataException)
        {
        }

        // What the reader above did not get to, or could not decode
        int decodedEnd = LastChildEnd;
        if (decodedEnd < End)
        {
            Add(new Unknown { Start = decodedEnd, Length = End - decodedEnd, Text = "Not decoded" });
        }
    }

    private void ParsePayload()
    {
        switch (Id)
        {
            case 0:
                ParseCustom();
                break;
            case 1:
                ParseVector("Type", ParseType);
                break;
            case 2:
                ParseVector("Import", ParseImport);
                break;
            case 3:
                ParseFunctionSection();
                break;
            case 4:
                ParseVector("Table", ParseTable);
                break;
            case 5:
                ParseVector("Memory", i => $"Memory {i}: {ReadLimits("pages")}");
                break;
            case 6:
                ParseVector("Global", ParseGlobal);
                break;
            case 7:
                ParseVector("Export", ParseExport);
                break;
            case 8:
                {
                    int start = position;
                    uint index = Leb();
                    Leaf(this, start, $"Start function: {File.DescribeFunction((int)index)}");
                    break;
                }

            case 9:
                ParseElements();
                break;
            case 10:
                ParseCode();
                break;
            case 11:
                ParseData();
                break;
            case 12:
                {
                    int start = position;
                    uint count = Leb();
                    Leaf(this, start, $"Data segment count: {count}");
                    break;
                }

            case 13:
                ParseVector("Tag", i =>
                {
                    position++;
                    return $"Tag {i}: type {Leb()}";
                });
                break;
        }
    }

    private uint Leb()
    {
        uint value = (uint)WasmReader.ReadLeb(bytes, ref position);
        CheckPosition();
        return value;
    }

    private void CheckPosition()
    {
        if (position > End)
        {
            throw new InvalidDataException("Read past the end of the section");
        }
    }

    private string ReadName()
    {
        string name = WasmReader.ReadName(bytes, ref position);
        CheckPosition();
        return name;
    }

    private Node Leaf(Node parent, int start, string text)
    {
        CheckPosition();
        if (position <= start)
        {
            return null;
        }

        var node = new Node { Start = start, Length = position - start, Text = text };
        parent.Add(node);
        return node;
    }

    /// <summary>
    /// A count followed by that many entries. The callback reads one entry
    /// and returns its text; the bytes it read become one node.
    /// </summary>
    private void ParseVector(string entryName, Func<int, string> readEntry)
    {
        int start = position;
        uint count = Leb();
        Leaf(this, start, $"{entryName} count: {count}");
        for (int i = 0; i < count; i++)
        {
            start = position;
            string text = readEntry(i);
            Leaf(this, start, text);
        }

        Text = $"{Name} section ({count:N0} entries)";
    }

    private string ReadValueType()
    {
        byte b = bytes[position++];
        switch (b)
        {
            case 0x7F: return "i32";
            case 0x7E: return "i64";
            case 0x7D: return "f32";
            case 0x7C: return "f64";
            case 0x7B: return "v128";
            case 0x70: return "funcref";
            case 0x6F: return "externref";
            case 0x69: return "exnref";
            case 0x63:
            case 0x64:
                long heapType = WasmReader.ReadSignedLeb(bytes, ref position);
                return b == 0x63 ? $"(ref null {heapType})" : $"(ref {heapType})";
            default:
                return $"0x{b:X2}";
        }
    }

    private string ParseType(int index)
    {
        const int FunctionType = 0x60;
        if (bytes[position++] != FunctionType)
        {
            throw new InvalidDataException("A type that is not a function type");
        }

        var parameters = new List<string>();
        uint parameterCount = Leb();
        for (int i = 0; i < parameterCount; i++)
        {
            parameters.Add(ReadValueType());
        }

        var results = new List<string>();
        uint resultCount = Leb();
        for (int i = 0; i < resultCount; i++)
        {
            results.Add(ReadValueType());
        }

        return $"Type {index}: ({string.Join(", ", parameters)}) -> ({string.Join(", ", results)})";
    }

    private string ReadLimits(string unit)
    {
        const int HasMaximum = 1;
        const int Is64Bit = 4;
        uint flags = Leb();
        ulong minimum = WasmReader.ReadLeb(bytes, ref position);
        lastLimitMinimum = minimum;
        string text = $"{minimum} {unit}";
        if ((flags & HasMaximum) != 0)
        {
            ulong maximum = WasmReader.ReadLeb(bytes, ref position);
            text += $", at most {maximum}";
        }

        if ((flags & Is64Bit) != 0)
        {
            text += ", 64-bit";
        }

        return text;
    }

    private string ParseImport(int index)
    {
        string module = ReadName();
        string field = ReadName();
        int kind = bytes[position++];
        string description;
        switch (kind)
        {
            case 0:
                description = $"function of type {Leb()}";
                File.FunctionNames[File.ImportedFunctionCount] = $"{module}.{field}";
                File.ImportedFunctionCount++;
                break;
            case 1:
                description = $"table of {ReadValueType()}, {ReadLimits("elements")}";
                break;
            case 2:
                description = $"memory, {ReadLimits("pages")}";
                break;
            case 3:
                string type = ReadValueType();
                description = bytes[position++] != 0 ? $"mutable global {type}" : $"global {type}";
                break;
            case 4:
                position++;
                description = $"tag of type {Leb()}";
                break;
            default:
                throw new InvalidDataException("Unknown import kind");
        }

        return $"Import {index}: {module}.{field} ({description})";
    }

    private string ParseTable(int index)
    {
        const int HasInitializer = 0x40;
        bool hasInitializer = bytes[position] == HasInitializer;
        if (hasInitializer)
        {
            position += 2;
        }

        string type = ReadValueType();
        string limits = ReadLimits("elements");
        File.FunctionTableSize = Math.Max(File.FunctionTableSize, (long)lastLimitMinimum);
        if (hasInitializer)
        {
            SkipConstantExpression(out _);
        }

        return $"Table {index}: {type}, {limits}";
    }

    private string ParseGlobal(int index)
    {
        string type = ReadValueType();
        bool isMutable = bytes[position++] != 0;
        SkipConstantExpression(out long? value);
        if (value.HasValue)
        {
            File.GlobalValues.Add(value.Value);
        }

        string valueText = value.HasValue ? $" = {value}" : "";
        return $"Global {index}: {(isMutable ? "mutable " : "")}{type}{valueText}";
    }

    private string ParseExport(int index)
    {
        string name = ReadName();
        int kind = bytes[position++];
        int target = (int)Leb();
        if (kind == 0 && !File.FunctionNames.ContainsKey(target))
        {
            File.FunctionNames[target] = name;
        }

        string kindName = kind < externalKinds.Length ? externalKinds[kind] : $"kind {kind}";
        return $"Export {index}: \"{name}\" = {kindName} {target}";
    }

    /// <summary>
    /// Skips an initializer expression up to and including its end opcode.
    /// The value is reported when the expression is a single integer constant.
    /// </summary>
    private void SkipConstantExpression(out long? value)
    {
        value = null;
        int instructions = 0;
        while (true)
        {
            byte opcode = bytes[position++];
            instructions++;
            switch (opcode)
            {
                case 0x0B:
                    if (instructions != 2)
                    {
                        value = null;
                    }

                    CheckPosition();
                    return;
                case 0x41:
                case 0x42:
                    value = WasmReader.ReadSignedLeb(bytes, ref position);
                    break;
                case 0x43:
                    position += 4;
                    break;
                case 0x44:
                    position += 8;
                    break;
                case 0x23:
                case 0xD2:
                    WasmReader.ReadLeb(bytes, ref position);
                    break;
                case 0xD0:
                    WasmReader.ReadSignedLeb(bytes, ref position);
                    break;
                case 0x6A:
                case 0x6B:
                case 0x6C:
                case 0x7C:
                case 0x7D:
                case 0x7E:
                    break;
                case 0xFD:
                    if (WasmReader.ReadLeb(bytes, ref position) != 0x0C)
                    {
                        throw new InvalidDataException("Unknown vector opcode in an initializer");
                    }

                    position += 16;
                    break;
                default:
                    throw new InvalidDataException("Unknown opcode in an initializer");
            }
        }
    }

    private void ParseCustom()
    {
        int start = position;
        string name = ReadName();
        Leaf(this, start, $"Section name: {name}");
        Text = $"custom section \"{name}\"";

        if (name == "name")
        {
            string[] subsections = ["Module name", "Function names", "Local names", "Label names", "Type names", "Table names", "Memory names", "Global names", "Element segment names", "Data segment names", "Field names", "Tag names"];
            while (position < End)
            {
                start = position;
                int id = bytes[position++];
                uint size = Leb();
                position += (int)size;
                Leaf(this, start, id < subsections.Length ? subsections[id] : $"Name subsection {id}");
            }

            return;
        }

        if (name == "linking")
        {
            string[] subsections = ["Subsection 0", "Subsection 1", "Subsection 2", "Subsection 3", "Subsection 4", "Segment info", "Init functions", "Comdat info", "Symbol table"];
            start = position;
            uint version = Leb();
            Leaf(this, start, $"Version: {version}");
            while (position < End)
            {
                start = position;
                int id = bytes[position++];
                uint size = Leb();
                position += (int)size;
                Leaf(this, start, id < subsections.Length ? subsections[id] : $"Linking subsection {id}");
            }

            return;
        }

        if (name.StartsWith("reloc.", StringComparison.Ordinal))
        {
            ParseRelocations(name);
            return;
        }

        if (End > position)
        {
            Add(new Region { Start = position, Length = End - position, Text = $"Contents of \"{name}\"" });
        }
    }

    /// <summary>
    /// A relocation section of an object file: the places in another section
    /// where the linker writes the final index or address of a symbol. Each
    /// entry has a type, the offset from the start of the contents of the
    /// target section, the position of the symbol in the symbol table, and
    /// for some types a number that is added to the address of the symbol.
    /// </summary>
    private void ParseRelocations(string name)
    {
        const int DataSection = 11;
        const int TableIndexI32 = 2;
        const int LastKnownType = 26;

        int start = position;
        uint sectionIndex = Leb();
        Leaf(this, start, $"Target section: {sectionIndex}");
        start = position;
        uint count = Leb();
        Leaf(this, start, $"Relocation count: {count}");

        var target = sectionIndex < File.Sections.Count ? File.Sections[(int)sectionIndex] : null;
        bool isData = target != null && target.Id == DataSection;
        start = position;
        for (uint i = 0; i < count; i++)
        {
            int type = bytes[position++];
            if (type > LastKnownType)
            {
                throw new InvalidDataException("Unknown relocation type");
            }

            uint offset = Leb();
            uint symbol = Leb();
            if (HasAddend(type))
            {
                WasmReader.ReadSignedLeb(bytes, ref position);
            }

            if (isData && type == TableIndexI32)
            {
                File.FunctionPointerRelocations[target.PayloadStart + (int)offset] = (int)symbol;
            }
        }

        Leaf(this, start, $"Relocation entries ({count:N0})");
        Text = $"custom section \"{name}\" ({count:N0} relocations)";
    }

    /// <summary>
    /// The relocation types for an address in memory or an offset in a
    /// function or a section.
    /// </summary>
    private static bool HasAddend(int type)
    {
        switch (type)
        {
            case 3:
            case 4:
            case 5:
            case 8:
            case 9:
            case 11:
            case 14:
            case 15:
            case 16:
            case 17:
            case 21:
            case 22:
            case 23:
            case 25:
                return true;
            default:
                return false;
        }
    }

    private void ParseFunctionSection()
    {
        int start = position;
        uint count = Leb();
        Leaf(this, start, $"Function count: {count}");
        start = position;
        for (int i = 0; i < count; i++)
        {
            Leb();
        }

        Leaf(this, start, "Type index of each function");
        Text = $"function section ({count:N0} entries)";
    }

    private void ParseElements()
    {
        int start = position;
        uint count = Leb();
        Leaf(this, start, $"Element segment count: {count}");
        for (int i = 0; i < count; i++)
        {
            int segmentStart = position;
            uint flags = Leb();
            bool isPassiveOrDeclarative = (flags & 1) != 0;
            bool hasTableIndex = (flags & 2) != 0;
            bool usesExpressions = (flags & 4) != 0;
            if (flags > 7)
            {
                throw new InvalidDataException("Unknown element segment kind");
            }

            // The position in the table of the first function of an active segment
            long? tableOffset = null;
            if (!isPassiveOrDeclarative)
            {
                uint tableIndex = hasTableIndex ? Leb() : 0;
                SkipConstantExpression(out long? offset);
                if (tableIndex == 0)
                {
                    tableOffset = offset;
                }
            }

            if (isPassiveOrDeclarative || hasTableIndex)
            {
                if (usesExpressions)
                {
                    ReadValueType();
                }
                else
                {
                    position++;
                }
            }

            uint itemCount = Leb();
            int itemsStart = position;
            for (int item = 0; item < itemCount; item++)
            {
                if (usesExpressions)
                {
                    SkipConstantExpression(out _);
                }
                else
                {
                    uint functionIndex = Leb();
                    if (tableOffset.HasValue)
                    {
                        File.TableFunctions[tableOffset.Value + item] = (int)functionIndex;
                    }
                }
            }

            int segmentEnd = position;
            var segment = new Node { Start = segmentStart, Length = segmentEnd - segmentStart, Text = $"Element segment {i} ({itemCount:N0} functions)" };
            Add(segment);
            segment.Add(new Node { Start = segmentStart, Length = itemsStart - segmentStart, Text = "Segment header" });
            if (segmentEnd > itemsStart)
            {
                segment.Add(new Node { Start = itemsStart, Length = segmentEnd - itemsStart, Text = "Function indices" });
            }
        }

        Text = $"element section ({count:N0} segments)";
    }

    private void ParseCode()
    {
        int start = position;
        uint count = Leb();
        Leaf(this, start, $"Function count: {count}");
        for (int i = 0; i < count; i++)
        {
            int functionStart = position;
            uint bodySize = Leb();
            int bodyStart = position;
            int bodyEnd = bodyStart + (int)bodySize;
            if (bodyEnd > End)
            {
                throw new InvalidDataException("Function body past the end of the section");
            }

            int index = File.ImportedFunctionCount + i;
            var function = new WasmFunction
            {
                Start = functionStart,
                Length = bodyEnd - functionStart,
                Index = index,
                Text = File.DescribeFunction(index)
            };
            Add(function);
            File.Functions.Add(function);
            function.Add(new Node { Start = functionStart, Length = bodyStart - functionStart, Text = $"Body size: {bodySize}" });

            uint groupCount = Leb();
            var locals = new List<string>();
            for (int group = 0; group < groupCount; group++)
            {
                uint localCount = Leb();
                locals.Add($"{localCount} {ReadValueType()}");
            }

            if (position > bodyEnd)
            {
                throw new InvalidDataException("Locals past the end of the function body");
            }

            function.Add(new Node { Start = bodyStart, Length = position - bodyStart, Text = locals.Count == 0 ? "Locals: none" : $"Locals: {string.Join(", ", locals)}" });
            if (bodyEnd > position)
            {
                int instructionCount = 0;
                bool isDecoded = WasmInstructions.Decode(bytes, position, bodyEnd, (_, _) => instructionCount++);
                var code = new WasmCode { Start = position, Length = bodyEnd - position, InstructionCount = isDecoded ? instructionCount : -1 };
                code.Text = isDecoded ? $"Code ({instructionCount:N0} instructions)" : "Code (with an instruction that is not known)";
                function.Add(code);
                if (!isDecoded)
                {
                    File.UndecodedFunctionCount++;
                }
            }

            position = bodyEnd;
        }

        Text = $"code section ({count:N0} functions)";
    }

    private void ParseData()
    {
        int start = position;
        uint count = Leb();
        Leaf(this, start, $"Data segment count: {count}");
        for (int i = 0; i < count; i++)
        {
            int segmentStart = position;
            uint flags = Leb();
            long address = -1;
            if (flags == 2)
            {
                Leb();
            }
            else if (flags > 2)
            {
                throw new InvalidDataException("Unknown data segment kind");
            }

            if (flags != 1)
            {
                SkipConstantExpression(out long? value);
                address = value.HasValue ? (uint)value.Value : -1;
            }

            uint size = Leb();
            int contentStart = position;
            if (contentStart + (long)size > End)
            {
                throw new InvalidDataException("Data segment past the end of the section");
            }

            string place = flags == 1 ? "passive" : address >= 0 ? $"at address 0x{address:X}" : "at a computed address";
            if (i < File.SegmentNames.Count)
            {
                place = $"\"{File.SegmentNames[i]}\"";
            }
            var segment = new WasmDataSegment
            {
                Start = segmentStart,
                Length = contentStart + (int)size - segmentStart,
                Index = i,
                Address = address,
                ContentStart = contentStart,
                ContentLength = (int)size,
                Text = $"Data segment {i}: {size:N0} bytes, {place}"
            };
            Add(segment);
            segment.Add(new Node { Start = segmentStart, Length = contentStart - segmentStart, Text = "Segment header" });
            File.DataSegments.Add(segment);
            position = contentStart + (int)size;
        }

        Text = $"data section ({count:N0} segments)";
    }
}

public class WasmFunction : Node
{
    public int Index { get; set; }

    /// <summary>
    /// The assembly whose method table lists the function, when the module
    /// has code that Mono's ahead-of-time compiler produced.
    /// </summary>
    public MonoAotModule Owner { get; set; }

    /// <summary>
    /// The index of the method in the method table of the assembly, or -1.
    /// An index below the number of rows of the MethodDef table of the
    /// assembly is the row; a higher one is a generic instance or a wrapper.
    /// </summary>
    public int MethodIndex { get; set; } = -1;

    /// <summary>
    /// The function unboxes the first argument and calls the method with
    /// the index above.
    /// </summary>
    public bool IsUnboxTrampoline { get; set; }
}

/// <summary>
/// The instructions of one function.
/// </summary>
public class WasmCode : Node
{
    public WasmCode()
    {
        Text = "Code";
    }

    /// <summary>
    /// Number of instructions, or -1 when the body has an instruction that
    /// the reader does not know.
    /// </summary>
    public int InstructionCount { get; set; }
}

public class WasmDataSegment : Node
{
    public int Index { get; set; }

    /// <summary>
    /// The address in linear memory, or -1 for a passive segment.
    /// </summary>
    public long Address { get; set; }

    public int ContentStart { get; set; }
    public int ContentLength { get; set; }
}
