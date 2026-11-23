using System.Collections.Generic;
using System.Linq;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    private const uint VisualBasicSignature = 0x21354256;

    /// <summary>
    /// Structures of a Visual Basic 5 or 6 program: the project header that
    /// the entry point passes to the runtime, the object table, and for each
    /// form, module and class its descriptors, names and controls. Field
    /// names are from Alex Ionescu's description of the format.
    /// </summary>
    private void AddVisualBasicStructures()
    {
        if (IsPE32Plus || Machine != MachineI386 || CLIHeader != null)
        {
            return;
        }

        int header = FindVisualBasicHeader();
        if (header <= 0)
        {
            return;
        }

        var project = AddVisualBasicRecord(
            header,
            "Visual Basic project header",
            [
                ("VB5! signature", 4),
                ("Runtime build", 2),
                ("Language DLL", 14),
                ("Secondary language DLL", 14),
                ("Runtime revision", 2),
                ("Language DLL LCID", 4),
                ("Secondary language DLL LCID", 4),
                ("Sub Main address", 4),
                ("Project data address", 4),
                ("Control flags (ids below 32)", 4),
                ("Control flags (ids above 32)", 4),
                ("Thread flags", 4),
                ("Thread count", 4),
                ("Form count", 2),
                ("External component count", 2),
                ("Thunk count", 4),
                ("GUI table address", 4),
                ("External table address", 4),
                ("COM register data address", 4),
                ("Project description offset", 4),
                ("Project exe name offset", 4),
                ("Project help file offset", 4),
                ("Project name offset", 4)
            ]);
        if (project == null)
        {
            return;
        }

        string projectName = null;
        for (int i = 19; i <= 22; i++)
        {
            int relative = project.ReadInt32(i);
            if (relative > 0)
            {
                string value = AddVisualBasicString(header + relative);
                if (i == 22)
                {
                    projectName = value;
                }
            }
        }

        if (projectName != null)
        {
            project.Text = $"Visual Basic project header: {projectName}";
        }

        int formCount = Buffer.ReadUInt16(project.Children[13].Start);
        int externalCount = Buffer.ReadUInt16(project.Children[14].Start);
        AddVisualBasicSizedRecords(ResolveAddressField(project, 16), formCount, isForm: true);
        AddVisualBasicSizedRecords(ResolveAddressField(project, 17), externalCount, isForm: false);

        AddVisualBasicComRegistration(ResolveAddressField(project, 18));

        var projectData = AddVisualBasicRecord(
            ResolveAddressField(project, 8),
            "Visual Basic project data",
            [
                ("Version", 4),
                ("Object table address", 4),
                ("Null", 4),
                ("Code start address", 4),
                ("Code end address", 4),
                ("Data size", 4),
                ("Thread space address", 4),
                ("VBA exception handler address", 4),
                ("Native code address", 4),
                ("Path information", 528),
                ("External table address", 4),
                ("External count", 4)
            ]);
        if (projectData == null)
        {
            return;
        }

        SetVisualBasicDataRange(header, projectData);
        AddVisualBasicExternalTable(projectData);
        AddVisualBasicObjects(projectData);
        AddVisualBasicStubs();
    }

    private void AddVisualBasicObjects(Record projectData)
    {
        var objectTable = AddVisualBasicRecord(
            ResolveAddressField(projectData, 1),
            "Visual Basic object table",
            [
                ("Heap link", 4),
                ("Exec project address", 4),
                ("Project info 2 address", 4),
                ("Reserved", 4),
                ("Null", 4),
                ("Project object address", 4),
                ("Object GUID", 16),
                ("Compile state", 2),
                ("Total objects", 2),
                ("Compiled objects", 2),
                ("Objects in use", 2),
                ("Object array address", 4),
                ("IDE flag", 4),
                ("IDE data", 4),
                ("IDE data 2", 4),
                ("Project name address", 4),
                ("LCID", 4),
                ("LCID 2", 4),
                ("IDE data 3", 4),
                ("Identifier", 4)
            ]);
        if (objectTable == null)
        {
            return;
        }

        AddVisualBasicString(ResolveAddressField(objectTable, 15));
        AddVisualBasicRecord(
            ResolveAddressField(objectTable, 2),
            "Visual Basic project info 2",
            [
                ("Heap link", 4),
                ("Object table address", 4),
                ("Reserved", 4),
                ("Unused", 4),
                ("Object list address", 4),
                ("Unused 2", 4),
                ("Project description address", 4),
                ("Project help file address", 4),
                ("Reserved 2", 4),
                ("Help context id", 4)
            ]);

        int objectCount = Buffer.ReadUInt16(objectTable.Children[8].Start);
        int objectArray = ResolveAddressField(objectTable, 11);
        for (int i = 0; i < objectCount && objectArray > 0; i++)
        {
            AddVisualBasicObject(objectArray + i * 48);
        }
    }

    private int FindVisualBasicHeader()
    {
        // The entry point of an EXE is "push <header address>; call ThunRTMain"
        int entryRva = OptionalHeader.StandardFields.AddressOfEntryPoint.Value;
        int entry = entryRva > 0 ? ResolveVirtualAddress(entryRva) : 0;
        if (entry > 0 && entry + 10 <= Buffer.Length && Buffer.ReadByte(entry) == 0x68 && Buffer.ReadByte(entry + 5) == 0xE8)
        {
            int header = ResolvePointer(Buffer.ReadUInt32(entry + 1));
            if (header > 0 && header + 104 <= Buffer.Length && Buffer.ReadUInt32(header) == VisualBasicSignature)
            {
                return header;
            }
        }

        // A DLL or OCX has no such entry point, so the signature is searched
        // for, but only in images that import the Visual Basic runtime
        if (!ImportNamesBySlotRva.Values.Any(name => name.StartsWith("MSVBVM", System.StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            if (raw <= 0 || size < 104 || (long)raw + size > Buffer.Length)
            {
                continue;
            }

            var bytes = Buffer.ReadBytes(raw, size);
            for (int i = 0; i + 104 <= bytes.Length; i += 4)
            {
                if (bytes[i] == 'V' && bytes[i + 1] == 'B' && bytes[i + 2] == '5' && bytes[i + 3] == '!' &&
                    ResolvePointer(System.BitConverter.ToUInt32(bytes, i + 48)) > 0)
                {
                    return raw + i;
                }
            }
        }

        return 0;
    }

    private int ResolveAddressField(Record record, int fieldIndex)
    {
        return ResolvePointer(Buffer.ReadUInt32(record.Children[fieldIndex].Start));
    }

    private int visualBasicDataStart;
    private int visualBasicDataEnd;
    private readonly HashSet<int> visualBasicProcedures = new();

    /// <summary>
    /// The code section of a Visual Basic image starts with the structures of
    /// the program, its strings, its P-code and short call stubs. Compiled
    /// code, if there is any, starts at the code start address of the project
    /// data. The bytes before that address are data unless a stub is found.
    /// </summary>
    private void SetVisualBasicDataRange(int header, Record projectData)
    {
        int codeStart = ResolveAddressField(projectData, 3);
        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            if (raw > 0 && header >= raw && header < raw + size && codeStart > header && codeStart <= raw + size)
            {
                visualBasicDataStart = raw;
                visualBasicDataEnd = codeStart;
            }
        }
    }

    /// <summary>
    /// Removes the Visual Basic data range from a byte range that is about
    /// to be labelled as native code.
    /// </summary>
    private Span ExcludeVisualBasicData(Span span)
    {
        if (span.Start < visualBasicDataStart || span.Start >= visualBasicDataEnd)
        {
            return span;
        }

        int start = System.Math.Min(span.End, visualBasicDataEnd);
        return new Span(start, span.End - start);
    }

    /// <summary>
    /// The external table lists the Declare statements and the imported
    /// type library objects of the project.
    /// </summary>
    private void AddVisualBasicExternalTable(Record projectData)
    {
        int count = projectData.ReadInt32(11);
        int offset = ResolveAddressField(projectData, 10);
        if (offset <= 0 || count <= 0 || count > 0x4000)
        {
            return;
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = count * 8,
            Count = count,
            Fields = [("Kind", 4), ("Data address", 4)],
            RecordText = "External entry"
        };
        if (!TryAdd(table))
        {
            return;
        }

        table.Text = $"Visual Basic external table ({count})";

        const int DeclaredFunction = 7;
        const int ImportedObject = 6;
        foreach (var entry in table.Children.OfType<Record>())
        {
            int kind = entry.ReadInt32(0);
            int data = ResolveAddressField(entry, 1);
            if (kind == DeclaredFunction)
            {
                var declaration = AddVisualBasicRecord(
                    data,
                    "Visual Basic declared function",
                    [
                        ("Library name address", 4),
                        ("Function name address", 4),
                        ("Unknown", 4),
                        ("Import data address", 4),
                        ("Unknown", 8)
                    ]);
                if (declaration != null)
                {
                    string library = AddVisualBasicCountedString(ResolveAddressField(declaration, 0));
                    string function = AddVisualBasicCountedString(ResolveAddressField(declaration, 1));
                    declaration.Text = $"Visual Basic declared function: {library}!{function}";
                    entry.Text = $"Declared function {library}!{function}";
                }
            }
            else if (kind == ImportedObject)
            {
                var imported = AddVisualBasicRecord(
                    data,
                    "Visual Basic imported object",
                    [("GUID address", 4), ("Unknown", 4)]);
                if (imported != null)
                {
                    AddVisualBasicGuid(ResolveAddressField(imported, 0), "Imported object GUID");
                    entry.Text = "Imported object";
                }
            }
        }
    }

    /// <summary>
    /// The names of a Declare statement are preceded by their length, which
    /// includes the zero terminator.
    /// </summary>
    private string AddVisualBasicCountedString(int offset)
    {
        string value = AddVisualBasicString(offset);
        if (value != null && offset >= 4 && Buffer.ReadInt32(offset - 4) == value.Length + 1 && IsRangeAvailable(offset - 4, 4))
        {
            Add(new FourBytes { Start = offset - 4, Text = "Name length" });
        }

        return value;
    }

    /// <summary>
    /// The COM registration data lists the public classes and controls of
    /// the project. Its offsets are relative to its own start.
    /// </summary>
    private void AddVisualBasicComRegistration(int offset)
    {
        var registration = AddVisualBasicRecord(
            offset,
            "Visual Basic COM registration data",
            [
                ("Registration info offset", 4),
                ("Project name offset", 4),
                ("Help directory offset", 4),
                ("Project description offset", 4),
                ("Project CLSID", 16),
                ("Type library LCID", 4),
                ("Unknown", 2),
                ("Type library major version", 2),
                ("Type library minor version", 2)
            ]);
        if (registration == null)
        {
            return;
        }

        for (int i = 1; i <= 3; i++)
        {
            int relative = registration.ReadInt32(i);
            if (relative > 0 && relative < 0x10000)
            {
                AddVisualBasicString(offset + relative);
            }
        }

        int next = registration.ReadInt32(0);
        for (int i = 0; i < 1000 && next > 0 && next < 0x100000; i++)
        {
            var info = AddVisualBasicRecord(
                offset + next,
                "Visual Basic COM registration info",
                [
                    ("Next info offset", 4),
                    ("Object name offset", 4),
                    ("Object description offset", 4),
                    ("Instancing", 4),
                    ("Object id", 4),
                    ("Object CLSID", 16),
                    ("Is interface", 4),
                    ("Object interface GUID offset", 4),
                    ("Events interface GUID offset", 4),
                    ("Has events", 4),
                    ("OLE misc status", 4),
                    ("Class type", 1),
                    ("Object type", 1),
                    ("Toolbox bitmap id", 2),
                    ("Default icon", 2),
                    ("Is designer", 2),
                    ("Designer data offset", 4)
                ]);
            if (info == null)
            {
                return;
            }

            int nameOffset = info.ReadInt32(1);
            string name = nameOffset > 0 && nameOffset < 0x100000 ? AddVisualBasicString(offset + nameOffset) : null;
            if (name != null)
            {
                info.Text = $"Visual Basic COM registration info: {name}";
            }

            int descriptionOffset = info.ReadInt32(2);
            if (descriptionOffset > 0 && descriptionOffset < 0x100000)
            {
                AddVisualBasicString(offset + descriptionOffset);
            }

            for (int field = 7; field <= 8; field++)
            {
                int guidOffset = info.ReadInt32(field);
                if (guidOffset > 0 && guidOffset < 0x100000)
                {
                    AddVisualBasicGuid(offset + guidOffset, field == 7 ? "Object interface GUID" : "Events interface GUID");
                }
            }

            next = info.ReadInt32(0);
        }
    }

    private void AddVisualBasicGuid(int offset, string text)
    {
        if (offset <= 0 || !IsRangeAvailable(offset, 16))
        {
            return;
        }

        var guid = new SixteenBytes { Start = offset };
        Add(guid);
        guid.Text = $"{text} {guid.ReadGuid()}";
    }

    /// <summary>
    /// A table of 4-byte addresses.
    /// </summary>
    private RecordTable AddVisualBasicAddressTable(int offset, int count, string text, string entryText)
    {
        if (offset <= 0 || count <= 0 || count > 0x4000 || (long)offset + count * 4 > Buffer.Length)
        {
            return null;
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = count * 4,
            Count = count,
            Fields = [(entryText, 4)]
        };
        if (!TryAdd(table))
        {
            return null;
        }

        table.Text = $"{text} ({count})";
        return table;
    }

    /// <summary>
    /// A procedure compiled to P-code is described by a record that the
    /// method table of its object points to. The P-code is located right
    /// before the record, and tables of local variables follow it.
    /// </summary>
    private void AddVisualBasicProcedure(int offset, int objectInfoOffset, string objectName)
    {
        const int HeaderSize = 24;
        const int TableHeaderSize = 12;
        if (offset <= 0 || offset + HeaderSize > Buffer.Length ||
            ResolvePointerAt(offset) != objectInfoOffset ||
            !visualBasicProcedures.Add(offset))
        {
            return;
        }

        int codeSize = Buffer.ReadUInt16(offset + 8);
        int lastTable = Buffer.ReadUInt16(offset + 10);

        // The tables are aligned to 4 bytes and each one starts with its
        // size. The header holds the offset of the last one.
        var tables = new List<(int size, int alignedSize, List<int> entrySizes)>();
        int position = HeaderSize;
        while (lastTable >= HeaderSize && position <= lastTable && tables.Count < 16)
        {
            int tableStart = offset + position;
            if (tableStart + TableHeaderSize > Buffer.Length)
            {
                break;
            }

            int size = Buffer.ReadUInt16(tableStart);
            int count = Buffer.ReadUInt16(tableStart + 6);
            int alignedSize = (size + 3) & ~3;
            if (size < TableHeaderSize || tableStart + alignedSize > Buffer.Length)
            {
                break;
            }

            // Most entries are 4 bytes, and one whose last byte is 1 has 6
            // more bytes. Other kinds have sizes that are not known here, so
            // a table with such entries is not split into entries.
            var entrySizes = new List<int>();
            int entry = TableHeaderSize;
            while (entrySizes.Count < count && entry + 4 <= size)
            {
                int entrySize = Buffer.ReadByte(tableStart + entry + 3) == 1 ? 10 : 4;
                entrySizes.Add(entrySize);
                entry += entrySize;
            }

            if (entrySizes.Count != count || entry != size)
            {
                entrySizes = null;
            }

            tables.Add((size, alignedSize, entrySizes));
            position += alignedSize;
        }

        if (position != lastTable + (tables.Count > 0 ? tables[tables.Count - 1].alignedSize : 0) || lastTable < HeaderSize)
        {
            // The chain of tables did not end at the last table
            tables.Clear();
            position = HeaderSize;
        }

        var procedure = new Region
        {
            Start = offset - codeSize,
            Length = codeSize + position,
            Text = $"Visual Basic procedure of {objectName}"
        };
        if (!TryAdd(procedure))
        {
            return;
        }

        if (codeSize > 0)
        {
            procedure.Add(new PCode { Start = procedure.Start, Length = codeSize });
        }

        procedure.Add(new Record
        {
            Start = offset,
            Length = HeaderSize,
            Text = "Procedure descriptor",
            Fields =
            [
                ("Object info address", 4),
                ("Argument size", 2),
                ("Frame size", 2),
                ("P-code size", 2),
                ("Offset of last cleanup table", 2),
                ("Unknown", 12)
            ]
        });

        int nextTable = offset + HeaderSize;
        foreach (var (size, alignedSize, entrySizes) in tables)
        {
            var fields = new List<(string, int)>
            {
                ("Table size", 2),
                ("Unknown", 4),
                ("Entry count", 2),
                ("Unknown", 4)
            };
            if (entrySizes == null)
            {
                if (size > TableHeaderSize)
                {
                    fields.Add(("Entries", size - TableHeaderSize));
                }
            }
            else
            {
                foreach (int entrySize in entrySizes)
                {
                    fields.Add(("Frame offset", 2));
                    fields.Add(("Kind", 2));
                    if (entrySize > 4)
                    {
                        fields.Add(("Additional data", entrySize - 4));
                    }
                }
            }

            if (alignedSize > size)
            {
                fields.Add(("Alignment", alignedSize - size));
            }

            procedure.Add(new Record
            {
                Start = nextTable,
                Length = alignedSize,
                Text = $"Cleanup table ({Buffer.ReadUInt16(nextTable + 6)})",
                Fields = fields.ToArray()
            });
            nextTable += alignedSize;
        }
    }

    private List<Span> GetVisualBasicGaps(Section section)
    {
        var gaps = new List<Span>();
        int position = section.Start;
        foreach (var child in section.Children)
        {
            int gapEnd = System.Math.Min(child.Start, visualBasicDataEnd);
            if (gapEnd > position)
            {
                gaps.Add(new Span(position, gapEnd - position));
            }

            position = System.Math.Max(position, child.End);
            if (position >= visualBasicDataEnd)
            {
                break;
            }
        }

        if (position < visualBasicDataEnd)
        {
            gaps.Add(new Span(position, visualBasicDataEnd - position));
        }

        return gaps;
    }

    /// <summary>
    /// A string constant is stored as a BSTR: the length in bytes, the
    /// UTF-16 text and a zero character.
    /// </summary>
    private void AddVisualBasicStrings(Section section)
    {
        foreach (var gap in GetVisualBasicGaps(section))
        {
            var bytes = Buffer.ReadBytes(gap.Start, gap.Length);
            int i = (4 - (gap.Start & 3)) & 3;
            while (i + 8 <= bytes.Length)
            {
                int length = MeasureVisualBasicString(bytes, i);
                if (length == 0)
                {
                    i += 4;
                    continue;
                }

                section.Add(new VisualBasicString { Start = gap.Start + i, Length = length });
                i += (length + 3) & ~3;
            }
        }
    }

    private static int MeasureVisualBasicString(byte[] bytes, int i)
    {
        int byteLength = System.BitConverter.ToInt32(bytes, i);
        if (byteLength < 2 || byteLength > 0x8000 || (byteLength & 1) != 0 || i + 4 + byteLength + 2 > bytes.Length)
        {
            return 0;
        }

        int end = i + 4 + byteLength;
        if (bytes[end] != 0 || bytes[end + 1] != 0)
        {
            return 0;
        }

        for (int c = i + 4; c < end; c += 2)
        {
            int character = bytes[c] | (bytes[c + 1] << 8);
            bool isText = (character >= 0x20 && character < 0xD800 && character != 0x7F) ||
                character == 9 || character == 10 || character == 13;
            if (!isText)
            {
                return 0;
            }
        }

        return byteLength + 6;
    }

    /// <summary>
    /// Short pieces of machine code are located between the tables: jumps
    /// through the import address table and stubs that load the address of a
    /// procedure descriptor and jump into the runtime.
    /// </summary>
    private void AddVisualBasicStubs()
    {
        if (visualBasicDataEnd <= visualBasicDataStart)
        {
            return;
        }

        int sectionIndex = FindChildIndex(visualBasicDataStart);
        if (sectionIndex < 0 || Children[sectionIndex] is not Section section)
        {
            return;
        }

        int entryRva = OptionalHeader.StandardFields.AddressOfEntryPoint.Value;
        int entry = entryRva > 0 ? ResolveVirtualAddress(entryRva) : 0;
        var descriptors = new List<int>();

        foreach (var gap in GetVisualBasicGaps(section))
        {
            var bytes = Buffer.ReadBytes(gap.Start, gap.Length);
            int i = 0;
            while (i < bytes.Length)
            {
                if (bytes[i] == 0xCC)
                {
                    int run = 1;
                    while (i + run < bytes.Length && bytes[i + run] == 0xCC)
                    {
                        run++;
                    }

                    if (run >= 4)
                    {
                        section.Add(new Padding { Start = gap.Start + i, Length = run });
                    }

                    i += run;
                    continue;
                }

                int stub = MeasureVisualBasicStub(bytes, i, gap.Start + i == entry, descriptors);
                if (stub == 0)
                {
                    i++;
                    continue;
                }

                int runStart = i;
                while (stub > 0)
                {
                    i += stub;
                    stub = i < bytes.Length ? MeasureVisualBasicStub(bytes, i, gap.Start + i == entry, descriptors) : 0;
                }

                section.Add(new NativeCode { Start = gap.Start + runStart, Length = i - runStart });
            }
        }

        // A stub loads the address of the descriptor of its procedure. This
        // finds the procedures that no method table lists.
        foreach (int descriptor in descriptors)
        {
            int objectInfo = ResolvePointerAt(descriptor);
            if (objectInfo > 0 && visualBasicObjectNames.TryGetValue(objectInfo, out string objectName))
            {
                AddVisualBasicProcedure(descriptor, objectInfo, objectName);
            }
        }

        AddVisualBasicStrings(section);
    }

    private readonly Dictionary<int, string> visualBasicObjectNames = new();

    private int MeasureVisualBasicStub(byte[] bytes, int i, bool isEntryPoint, List<int> descriptors)
    {
        int remaining = bytes.Length - i;
        byte first = bytes[i];

        bool IsImageAddress(int at) => ResolvePointer(System.BitConverter.ToUInt32(bytes, at)) > 0;
        void AddDescriptor(int at)
        {
            int descriptor = ResolvePointer(System.BitConverter.ToUInt32(bytes, at));
            if (descriptor > 0)
            {
                descriptors.Add(descriptor);
            }
        }

        // jmp [import address table slot]
        if (first == 0xFF && remaining >= 6 && bytes[i + 1] == 0x25 &&
            ImportNamesBySlotRva.ContainsKey(VirtualAddressToRva(System.BitConverter.ToUInt32(bytes, i + 2))))
        {
            return 6;
        }

        // mov edx, descriptor; mov ecx, address; jmp ecx
        if (first == 0xBA && remaining >= 12 && bytes[i + 5] == 0xB9 && bytes[i + 10] == 0xFF && bytes[i + 11] == 0xE1 &&
            IsImageAddress(i + 6))
        {
            AddDescriptor(i + 1);
            return 12;
        }

        // add or sub dword [esp+4], offset; then a jump
        if (first == 0x81 && remaining >= 13 && (bytes[i + 1] == 0x44 || bytes[i + 1] == 0x6C) && bytes[i + 2] == 0x24 && bytes[i + 3] == 0x04)
        {
            if (remaining >= 15 && bytes[i + 8] == 0xB9 && bytes[i + 13] == 0xFF && bytes[i + 14] == 0xE1 && IsImageAddress(i + 9))
            {
                return 15;
            }

            if (bytes[i + 8] == 0xE9)
            {
                return 13;
            }
        }

        // mov eax, offset; cmp ax, 0xC033; mov edx, descriptor; push address; ret
        if (first == 0xB8 && remaining >= 20 && bytes[i + 5] == 0x66 && bytes[i + 6] == 0x3D &&
            bytes[i + 7] == 0x33 && bytes[i + 8] == 0xC0 && bytes[i + 9] == 0xBA &&
            bytes[i + 14] == 0x68 && bytes[i + 19] == 0xC3 && IsImageAddress(i + 15))
        {
            AddDescriptor(i + 10);
            return 20;
        }

        // xor eax, eax; mov edx, descriptor; push address; ret
        if (first == 0x33 && remaining >= 13 && bytes[i + 1] == 0xC0 && bytes[i + 2] == 0xBA &&
            bytes[i + 7] == 0x68 && bytes[i + 12] == 0xC3 && IsImageAddress(i + 8))
        {
            AddDescriptor(i + 3);
            return 13;
        }

        // The stub of a Declare statement:
        // mov eax, [slot]; or eax, eax; jz +2; jmp eax;
        // push declaration; mov eax, DllFunctionCall; call eax; jmp eax
        if (first == 0xA1 && remaining >= 25 && bytes[i + 5] == 0x0B && bytes[i + 6] == 0xC0 &&
            bytes[i + 7] == 0x74 && bytes[i + 8] == 0x02 && bytes[i + 9] == 0xFF && bytes[i + 10] == 0xE0 &&
            bytes[i + 11] == 0x68 && bytes[i + 16] == 0xB8 &&
            bytes[i + 21] == 0xFF && bytes[i + 22] == 0xD0 && bytes[i + 23] == 0xFF && bytes[i + 24] == 0xE0)
        {
            return 25;
        }

        // push header; call ThunRTMain
        if (isEntryPoint && first == 0x68 && remaining >= 10 && bytes[i + 5] == 0xE8)
        {
            return 10;
        }

        return 0;
    }

    private Record AddVisualBasicRecord(int offset, string text, (string, int)[] fields)
    {
        int size = fields.Sum(f => f.Item2);
        if (offset <= 0 || (long)offset + size > Buffer.Length)
        {
            return null;
        }

        var record = new Record { Start = offset, Length = size, Fields = fields };
        if (!TryAdd(record))
        {
            return null;
        }

        record.Text = text;
        return record;
    }

    private string AddVisualBasicString(int offset)
    {
        string value = ReadZeroTerminatedAscii(offset);
        if (value == null || !IsRangeAvailable(offset, value.Length + 1))
        {
            return value;
        }

        Add(new ZeroTerminatedString { Start = offset });
        return value;
    }

    /// <summary>
    /// The form table and the external component table hold records that
    /// start with their own size.
    /// </summary>
    private void AddVisualBasicSizedRecords(int offset, int count, bool isForm)
    {
        for (int i = 0; i < count && offset > 0 && offset + 4 <= Buffer.Length; i++)
        {
            int size = Buffer.ReadInt32(offset);
            int minimumSize = isForm ? 76 : 52;
            if (size < minimumSize || size > 0x1000)
            {
                return;
            }

            (string, int)[] fields = isForm
                ?
                [
                    ("Structure size", 4),
                    ("Object GUI GUID", 16),
                    ("Unknown", 16),
                    ("Object id", 4),
                    ("Unknown", 4),
                    ("OLE misc flags", 4),
                    ("Object GUID", 16),
                    ("Size of form body", 4),
                    ("Unknown", 4),
                    ("Form body address", 4),
                    ("Remaining", size - 76)
                ]
                : GetExternalComponentFields(offset, size);
            var record = AddVisualBasicRecord(offset, isForm ? "Visual Basic form info" : "Visual Basic external component info", fields);
            if (record == null)
            {
                return;
            }

            if (isForm)
            {
                int bodySize = record.ReadInt32(7);
                int body = ResolveAddressField(record, 9);
                if (body > 0 && bodySize > 0 && bodySize < 0x1000000)
                {
                    TryAddRegion(new Region { Start = body, Length = bodySize, Text = "Visual Basic form data" });
                }
            }

            offset += size;
        }
    }

    /// <summary>
    /// The fixed part of an external component record is followed by a GUID
    /// and three strings, which the fixed part locates by offset.
    /// </summary>
    private (string, int)[] GetExternalComponentFields(int offset, int size)
    {
        const int FixedSize = 52;
        var fields = new List<(string, int)>
        {
            ("Structure size", 4),
            ("GUID offset", 4),
            ("Unknown", 20),
            ("GUID offset 2", 4),
            ("GUID length", 4),
            ("Unknown", 4),
            ("File name offset", 4),
            ("Source offset", 4),
            ("Name offset", 4)
        };

        var items = new List<(int start, int length, string text)>();
        int guidOffset = Buffer.ReadInt32(offset + 4);
        if (guidOffset >= FixedSize && guidOffset + 16 <= size)
        {
            items.Add((guidOffset, 16, "Component GUID"));
        }

        string[] names = ["File name", "Source name", "Name"];
        for (int i = 0; i < names.Length; i++)
        {
            int stringOffset = Buffer.ReadInt32(offset + 40 + i * 4);
            string value = stringOffset >= FixedSize && stringOffset < size ? ReadZeroTerminatedAscii(offset + stringOffset) : null;
            if (value != null && stringOffset + value.Length + 1 <= size)
            {
                items.Add((stringOffset, value.Length + 1, value.Length == 0 ? names[i] : $"{names[i]}: {value}"));
            }
        }

        int position = FixedSize;
        foreach (var (start, length, text) in items.OrderBy(item => item.start))
        {
            if (start < position)
            {
                continue;
            }

            if (start > position)
            {
                fields.Add(("Unknown", start - position));
            }

            fields.Add((text, length));
            position = start + length;
        }

        if (position < size)
        {
            fields.Add(("Remaining", size - position));
        }

        return fields.ToArray();
    }

    private void AddVisualBasicObject(int offset)
    {
        var descriptor = AddVisualBasicRecord(
            offset,
            "Visual Basic object descriptor",
            [
                ("Object info address", 4),
                ("Reserved", 4),
                ("Public bytes address", 4),
                ("Static bytes address", 4),
                ("Module public address", 4),
                ("Module static address", 4),
                ("Object name address", 4),
                ("Method count", 4),
                ("Method names address", 4),
                ("Static variables offset", 4),
                ("Object type", 4),
                ("Null", 4)
            ]);
        if (descriptor == null)
        {
            return;
        }

        string name = AddVisualBasicString(ResolveAddressField(descriptor, 6)) ?? "?";
        descriptor.Text = $"Visual Basic object descriptor: {name}";

        int methodCount = descriptor.ReadInt32(7);
        int methodNames = ResolveAddressField(descriptor, 8);
        if (methodNames > 0 && methodCount > 0 && methodCount < 0x4000)
        {
            var names = new RecordTable
            {
                Start = methodNames,
                Length = methodCount * 4,
                Count = methodCount,
                Fields = [("Method name address", 4)]
            };
            if (TryAdd(names))
            {
                names.Text = $"Visual Basic method names: {name} ({methodCount})";
                foreach (var entry in names.Children)
                {
                    string methodName = AddVisualBasicString(ResolvePointer(Buffer.ReadUInt32(entry.Start)));
                    if (methodName != null)
                    {
                        entry.Text = methodName;
                    }
                }
            }
        }

        int objectInfoOffset = ResolveAddressField(descriptor, 0);
        var objectInfo = AddVisualBasicRecord(
            objectInfoOffset,
            $"Visual Basic object info: {name}",
            [
                ("Reference count", 2),
                ("Object index", 2),
                ("Object table address", 4),
                ("IDE data", 4),
                ("Private object address", 4),
                ("Reserved", 4),
                ("Null", 4),
                ("Object address", 4),
                ("Project data address", 4),
                ("Method count", 2),
                ("Method count 2", 2),
                ("Methods address", 4),
                ("Constant count", 2),
                ("Maximum constants", 2),
                ("IDE data 2", 4),
                ("IDE data 3", 4),
                ("Constants address", 4)
            ]);
        if (objectInfo == null)
        {
            return;
        }

        visualBasicObjectNames[objectInfoOffset] = name;
        AddVisualBasicAddressTable(
            ResolveAddressField(objectInfo, 16),
            Buffer.ReadUInt16(objectInfo.Children[12].Start),
            $"Visual Basic constant pool: {name}",
            "Constant");
        var methods = AddVisualBasicAddressTable(
            ResolveAddressField(objectInfo, 11),
            Buffer.ReadUInt16(objectInfo.Children[9].Start),
            $"Visual Basic method table: {name}",
            "Procedure descriptor address");
        if (methods != null)
        {
            foreach (var method in methods.Children)
            {
                AddVisualBasicProcedure(ResolvePointerAt(method.Start), objectInfoOffset, name);
            }
        }

        AddVisualBasicRecord(
            ResolveAddressField(objectInfo, 4),
            $"Visual Basic private object descriptor: {name}",
            [
                ("Heap link", 4),
                ("Object info address", 4),
                ("Reserved", 4),
                ("IDE data", 12),
                ("Object list address", 4),
                ("IDE data 2", 4),
                ("Object list 2 address", 12),
                ("IDE data 3", 12),
                ("Object type", 4),
                ("Identifier", 4)
            ]);

        const int HasOptionalInfo = 2;
        if ((descriptor.ReadInt32(10) & HasOptionalInfo) == 0)
        {
            return;
        }

        var optionalInfo = AddVisualBasicRecord(
            objectInfoOffset + 56,
            $"Visual Basic optional object info: {name}",
            [
                ("Object GUID count", 4),
                ("Object GUID address", 4),
                ("Null", 4),
                ("Object type GUIDs address", 4),
                ("Object type GUID count", 4),
                ("Controls 2 address", 4),
                ("Null 2", 4),
                ("Object GUID 2 address", 4),
                ("Control count", 4),
                ("Controls address", 4),
                ("Event count", 2),
                ("P-code count", 2),
                ("Initialize event offset", 2),
                ("Terminate event offset", 2),
                ("Events address", 4),
                ("Basic class object address", 4),
                ("Null 3", 4),
                ("IDE data", 4)
            ]);
        if (optionalInfo == null)
        {
            return;
        }

        int objectGuidCount = optionalInfo.ReadInt32(0);
        AddVisualBasicGuid(ResolveAddressField(optionalInfo, 1), "Object GUID");
        AddVisualBasicGuidAddressTable(
            ResolveAddressField(optionalInfo, 3),
            optionalInfo.ReadInt32(4),
            $"Visual Basic object type GUID addresses: {name}",
            "Object type GUID");
        AddVisualBasicGuidAddressTable(
            ResolveAddressField(optionalInfo, 7),
            objectGuidCount,
            $"Visual Basic object GUID addresses: {name}",
            "Object GUID");
        AddVisualBasicAddressTable(
            ResolveAddressField(optionalInfo, 14),
            Buffer.ReadUInt16(optionalInfo.Children[10].Start),
            $"Visual Basic method link table: {name}",
            "Stub address");

        int controlCount = optionalInfo.ReadInt32(8);
        int controls = ResolveAddressField(optionalInfo, 9);
        for (int i = 0; i < controlCount && controlCount < 0x1000 && controls > 0; i++)
        {
            var control = AddVisualBasicRecord(
                controls + i * 40,
                "Visual Basic control info",
                [
                    ("Control type", 2),
                    ("Event count", 2),
                    ("Events offset", 4),
                    ("GUID address", 4),
                    ("Index", 4),
                    ("Null", 4),
                    ("Null 2", 4),
                    ("Event table address", 4),
                    ("IDE data", 4),
                    ("Name address", 4),
                    ("Index copy", 4)
                ]);
            if (control == null)
            {
                break;
            }

            string controlName = AddVisualBasicString(ResolveAddressField(control, 9));
            if (controlName != null)
            {
                control.Text = $"Visual Basic control info: {controlName}";
            }

            AddVisualBasicGuid(ResolveAddressField(control, 3), "Control GUID");

            int eventCount = Buffer.ReadUInt16(control.Children[1].Start);
            var fields = new List<(string, int)>
            {
                ("Null", 4),
                ("Control info address", 4),
                ("Object info address", 4),
                ("QueryInterface address", 4),
                ("AddRef address", 4),
                ("Release address", 4)
            };
            for (int e = 0; e < eventCount; e++)
            {
                fields.Add(("Event handler address", 4));
            }

            int eventTable = ResolveAddressField(control, 7);
            if (eventTable > 0 && ResolvePointerAt(eventTable + 4) == control.Start)
            {
                AddVisualBasicRecord(
                    eventTable,
                    $"Visual Basic event table: {controlName ?? name} ({eventCount})",
                    fields.ToArray());
            }
        }
    }

    private void AddVisualBasicGuidAddressTable(int offset, int count, string text, string guidText)
    {
        var table = AddVisualBasicAddressTable(offset, count, text, "GUID address");
        if (table == null)
        {
            return;
        }

        foreach (var entry in table.Children)
        {
            AddVisualBasicGuid(ResolvePointerAt(entry.Start), guidText);
        }
    }
}

public class VisualBasicString : Node
{
    public override void Parse()
    {
        int byteLength = Length - 6;
        AddFourBytes("Length in bytes");
        Add(new Utf16String { Length = byteLength });
        AddTwoBytes("Zero");
        Text = System.Text.Encoding.Unicode.GetString(Buffer.ReadBytes(Start + 4, byteLength));
    }
}

/// <summary>
/// The byte code of one Visual Basic procedure.
/// </summary>
public class PCode : Node
{
    public PCode()
    {
        Text = "P-code";
    }
}
