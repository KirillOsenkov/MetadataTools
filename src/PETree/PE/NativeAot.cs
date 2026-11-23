using System;
using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    public List<NativeAotModuleHeader> NativeAotModuleHeaders { get; } = new();

    public bool IsNativeAot => NativeAotModuleHeaders.Count > 0;

    /// <summary>
    /// A NativeAOT executable has no CLI header. The compiler writes a module
    /// header ("RTR" signature) into read-only data: a table of section ids
    /// with the start and end address of each runtime data region.
    /// </summary>
    private void AddNativeAotModules()
    {
        if (CLIHeader != null)
        {
            return;
        }

        const uint ImageScnCntCode = 0x00000020;
        int rowSize = 8 + 2 * PointerSize;
        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            if (raw <= 0 || size < 16 || (long)raw + size > Buffer.Length ||
                ((uint)sectionHeader.Characteristics.Value & ImageScnCntCode) != 0)
            {
                continue;
            }

            var bytes = Buffer.ReadBytes(raw, size);
            for (int i = 0; i + 16 <= bytes.Length; i += 4)
            {
                if (bytes[i] != 'R' || bytes[i + 1] != 'T' || bytes[i + 2] != 'R' || bytes[i + 3] != 0)
                {
                    continue;
                }

                int sectionCount = BitConverter.ToUInt16(bytes, i + 12);
                int entrySize = bytes[i + 14];
                int entryType = bytes[i + 15];
                int length = 16 + sectionCount * entrySize;
                if (entryType != 1 || entrySize != rowSize || sectionCount == 0 || sectionCount > 512 || i + length > bytes.Length)
                {
                    continue;
                }

                var header = new NativeAotModuleHeader { Start = raw + i, Length = length, PointerSize = PointerSize };
                if (TryAdd(header))
                {
                    NativeAotModuleHeaders.Add(header);
                }
            }
        }

        foreach (var header in NativeAotModuleHeaders)
        {
            foreach (var row in header.Rows)
            {
                AddNativeAotRegion(row);
            }
        }
    }

    private void AddNativeAotRegion(Record row)
    {
        int id = row.ReadInt32(0);
        int flags = row.ReadInt32(1);
        ulong start = ReadPointer(row.Children[2].Start);
        ulong end = ReadPointer(row.Children[3].Start);
        string name = ReadyToRunSection.GetSectionTypeName(id);
        int startRva = VirtualAddressToRva(start);
        const int HasEndPointer = 1;
        bool hasEnd = (flags & HasEndPointer) != 0 && end >= start;
        long size = hasEnd ? (long)(end - start) : PointerSize;
        row.Text = $"{name}: RVA 0x{startRva:X}, {size} bytes";

        int offset = startRva > 0 ? ResolveVirtualAddress(startRva) : 0;
        if (offset <= 0)
        {
            row.Text += " (no file data)";
            return;
        }

        if (size <= 0)
        {
            return;
        }

        var sectionHeader = GetSectionAtVirtualAddress(startRva);
        int rawEnd = sectionHeader.PointerToRawData.Value + sectionHeader.SizeOfRawData.Value;
        int length = (int)Math.Min(size, rawEnd - offset);
        string text = $"NativeAOT {name}";

        const int DehydratedDataSection = 207;
        if (id == DehydratedDataSection)
        {
            AddDehydratedData(offset, hasEnd ? length : 0, rawEnd);
            return;
        }

        Node node = CreateNativeAotNode(id, offset, length);
        if (node != null)
        {
            node.Start = offset;
            node.Length = length;
            if (TryAdd(node))
            {
                node.Text = node switch
                {
                    NativeHashtable hashtable => $"{text} ({hashtable.EntryCount} entries)",
                    RecordTable table => $"{text} ({table.Count} entries)",
                    FrozenObjectRegion frozen => $"{text} ({frozen.ObjectCount} objects)",
                    _ => text
                };
                return;
            }
        }

        TryAddRegion(new Region { Start = offset, Length = length, Text = text });
    }

    /// <summary>
    /// Labels MethodTables: the type descriptors of the NativeAOT runtime.
    /// Candidates are the types of frozen objects and the targets of the
    /// reference tables; from each one the base type and the interfaces are
    /// visited too. A candidate is accepted when every vtable slot is a
    /// relocated pointer to code and every interface slot a relocated pointer
    /// to data.
    /// </summary>
    private void AddNativeAotMethodTables()
    {
        if (!IsNativeAot || !HasRelocations || PointerSize != 8)
        {
            return;
        }

        var pending = new Stack<int>();
        var visited = new HashSet<int>();

        void Enqueue(int offset)
        {
            if (offset > 0 && visited.Add(offset))
            {
                pending.Push(offset);
            }
        }

        foreach (var node in Inspect(this))
        {
            if (node is FrozenObjectRegion frozen)
            {
                foreach (int offset in frozen.MethodTableOffsets)
                {
                    Enqueue(offset);
                }
            }
            else if (node is RecordTable table && table.Text != null &&
                (table.Text.StartsWith("NativeAOT CommonFixupsTable") || table.Text.StartsWith("NativeAOT NativeReferences")))
            {
                foreach (var record in table.Children)
                {
                    int target = record.Length == 4
                        ? ResolveVirtualAddress(OffsetToRva(record.Start) + Buffer.ReadInt32(record.Start))
                        : ResolvePointerAt(record.Start);
                    Enqueue(target);
                }
            }
        }

        int added = 0;
        while (pending.Count > 0 && added < 200000)
        {
            int offset = pending.Pop();
            if ((offset & 7) != 0 || offset + 24 > Buffer.Length || !IsRangeAvailable(offset, 24))
            {
                continue;
            }

            uint baseSize = Buffer.ReadUInt32(offset + 4);
            int vtableSlots = Buffer.ReadUInt16(offset + 16);
            int interfaces = Buffer.ReadUInt16(offset + 18);
            int size = 24 + (vtableSlots + interfaces) * 8;
            if (baseSize > 0x1000000 || !IsRangeAvailable(offset, size))
            {
                continue;
            }

            bool hasRelatedType = IsRelocated(offset + 8);
            if (!hasRelatedType && Buffer.ReadUInt64(offset + 8) != 0 && vtableSlots + interfaces == 0)
            {
                continue;
            }

            if (vtableSlots + interfaces == 0 && !hasRelatedType)
            {
                continue;
            }

            bool isValid = true;
            for (int i = 0; i < vtableSlots + interfaces && isValid; i++)
            {
                int slot = offset + 24 + i * 8;
                ulong value = Buffer.ReadUInt64(slot);
                bool isVtableSlot = i < vtableSlots;
                if (value == 0 && isVtableSlot)
                {
                    // A slot of a method that was removed as unused
                    continue;
                }

                int rva = IsRelocated(slot) ? VirtualAddressToRva(value) : 0;
                isValid = rva != 0 && IsCodeAddress(rva) == isVtableSlot;
            }

            if (!isValid)
            {
                continue;
            }

            var fields = new List<(string, int)>
            {
                ("Flags", 4),
                ("Base size", 4),
                ("Related type", 8),
                ("Vtable slot count", 2),
                ("Interface count", 2),
                ("Hash code", 4)
            };
            for (int i = 0; i < vtableSlots; i++)
            {
                fields.Add(($"Vtable slot {i}", 8));
            }

            for (int i = 0; i < interfaces; i++)
            {
                fields.Add(($"Interface {i}", 8));
            }

            var methodTable = new Record { Start = offset, Length = size, Fields = fields.ToArray() };
            Add(methodTable);
            methodTable.Text = $"MethodTable (base size {baseSize}, {vtableSlots} vtable slots, {interfaces} interfaces)";
            added++;

            if (hasRelatedType)
            {
                Enqueue(ResolvePointerAt(offset + 8));
            }

            for (int i = 0; i < interfaces; i++)
            {
                Enqueue(ResolvePointerAt(offset + 24 + (vtableSlots + i) * 8));
            }
        }
    }

    private static IEnumerable<Node> Inspect(Node node)
    {
        yield return node;
        if (node.HasChildren && (node is PEFile || node is Section || node is Region))
        {
            foreach (var child in node.Children)
            {
                foreach (var descendant in Inspect(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private Node CreateNativeAotNode(int id, int offset, int length)
    {
        string DescribeRelativePointer(Record record)
        {
            int start = record.Start;
            int target = OffsetToRva(start) + Buffer.ReadInt32(start);
            return $"Relative pointer to 0x{target:X}";
        }

        switch (id)
        {
            // Hashtables of the reflection and type loader maps
            case 301:
            case 302:
            case 303:
            case 304:
            case 306:
            case 307:
            case 309:
            case 310:
            case 311:
            case 316:
            case 317:
            case 318:
            case 319:
            case 321:
            case 322:
            case 324:
            case 332:
            case 334:
            case 335:
            case 336:
                return new NativeHashtable();

            // Arrays of references: pointers when relocated, else 32-bit relative pointers
            case 201:
            case 205:
            case 213:
            case 308:
            case 331:
            case 333:
                if (IsRelocated(offset) && length % PointerSize == 0)
                {
                    return new RecordTable { Count = length / PointerSize, Fields = [("Pointer", PointerSize)], RecordText = "Pointer" };
                }

                if (length % 4 != 0)
                {
                    return null;
                }

                return new RecordTable
                {
                    Count = length / 4,
                    Fields = [("Relative pointer", 4)],
                    Describe = DescribeRelativePointer
                };
            case 313:
                return length > 4 && Buffer.ReadUInt32(offset) == 0xDEADDFFD
                    ? new Record { Fields = [("Signature", 4), ("Metadata records", length - 4)] }
                    : null;
            case 206:
                return new FrozenObjectRegion { PEFile = this };
            default:
                return null;
        }
    }

    /// <summary>
    /// Older compilers give the end of the command stream in the module
    /// header row; newer ones store the length after the first four bytes.
    /// In both the table of relocation targets follows the commands.
    /// </summary>
    private void AddDehydratedData(int offset, int knownLength, int rawEnd)
    {
        bool hasLengthField = knownLength == 0;
        int headerSize = hasLengthField ? 8 : 4;
        int commandsEnd = hasLengthField ? offset + Buffer.ReadInt32(offset + 4) : offset + knownLength;
        if (commandsEnd <= offset + headerSize || commandsEnd > rawEnd)
        {
            return;
        }

        var commands = Buffer.ReadBytes(offset + headerSize, commandsEnd - offset - headerSize);
        int fixupCount = 0;
        foreach (var command in DehydratedData.Decode(commands))
        {
            if ((command.kind == 2 || command.kind == 3) && command.payload >= fixupCount)
            {
                fixupCount = command.payload + 1;
            }
        }

        int fixupsEnd = Math.Min(commandsEnd + fixupCount * 4, rawEnd);
        TryAdd(new DehydratedData
        {
            Start = offset,
            Length = fixupsEnd - offset,
            HeaderSize = headerSize,
            CommandsLength = commands.Length
        });
    }

    /// <summary>
    /// In a NativeAOT image the unwind info of a managed method is followed
    /// by a flags byte, optional RVAs of associated data and exception
    /// handling info, and for the main body of a method the GC info.
    /// </summary>
    private void AddNativeAotUnwindData(ExceptionTable table)
    {
        if (!IsNativeAot || table == null || Machine != MachineAmd64)
        {
            return;
        }

        const int MaximumBlobSize = 0x4000;
        foreach (var unwindInfo in table.PlainUnwindInfos)
        {
            int start = unwindInfo.End;
            int nextUnwindInfo = (start + 3) & ~3;
            if (table.UnwindOffsets.Contains(nextUnwindInfo) || table.UnwindOffsets.Contains(start))
            {
                continue;
            }

            int available = GetFreeLength(start, MaximumBlobSize);
            if (available < 2)
            {
                continue;
            }

            byte flags = Buffer.ReadByte(start);
            const int KindMask = 0x03;
            const int HasEHInfo = 0x04;
            const int HasAssociatedData = 0x10;
            if ((flags & 0xE0) != 0 || (flags & KindMask) == KindMask)
            {
                continue;
            }

            int headerSize = 1 + ((flags & HasAssociatedData) != 0 ? 4 : 0) + ((flags & HasEHInfo) != 0 ? 4 : 0);
            if (headerSize > available)
            {
                continue;
            }

            Add(new NativeAotUnwindData { Start = start, Length = available, Flags = flags });
        }
    }
}

/// <summary>
/// The module header the NativeAOT compiler emits: version, then one row
/// per runtime data region.
/// </summary>
public class NativeAotModuleHeader : Node
{
    public int PointerSize { get; set; }

    public override void Parse()
    {
        AddFourBytes("Signature (RTR)");
        var major = AddTwoBytes("Major version");
        var minor = AddTwoBytes("Minor version");
        AddFourBytes("Flags");
        var count = AddTwoBytes("Number of sections");
        AddOneByte("Entry size");
        AddOneByte("Entry type");

        var rows = new List<Record>();
        for (int i = 0; i < (ushort)count.Value; i++)
        {
            var row = new Record
            {
                Fields = [("Section id", 4), ("Flags", 4), ("Start", PointerSize), ("End", PointerSize)]
            };
            Add(row);
            rows.Add(row);
        }

        Rows = rows;
        Text = $"NativeAOT module header {major.Value}.{minor.Value}";
    }

    public IReadOnlyList<Record> Rows { get; set; }
}

/// <summary>
/// Data that the startup code expands into memory: a stream of copy, zero
/// fill and relocation commands, followed by the table of relocation targets.
/// </summary>
public class DehydratedData : Node
{
    public int HeaderSize { get; set; }
    public int CommandsLength { get; set; }

    /// <summary>
    /// Splits a command stream into commands. Kinds: 0 copy, 1 zero fill,
    /// 2 relative pointer, 3 pointer, 4 inline relative pointers, 5 inline
    /// pointers. The length includes the bytes a command carries.
    /// </summary>
    public static IEnumerable<(int offset, int length, int kind, int payload)> Decode(byte[] bytes)
    {
        const int MaxShortPayload = 28;
        int position = 0;
        while (position < bytes.Length)
        {
            int commandStart = position;
            byte b = bytes[position++];
            int kind = b & 7;
            int payload = b >> 3;
            int extraBytes = payload - MaxShortPayload;
            if (extraBytes > 0)
            {
                payload = 0;
                for (int i = 0; i < extraBytes && position < bytes.Length; i++)
                {
                    payload |= bytes[position++] << (8 * i);
                }

                payload += MaxShortPayload;
            }

            if (kind == 0)
            {
                position += payload;
            }
            else if (kind == 4 || kind == 5)
            {
                position += payload * 4;
            }
            else if (kind > 5)
            {
                position = bytes.Length;
            }

            if (position > bytes.Length || position < commandStart)
            {
                position = bytes.Length;
            }

            yield return (commandStart, position - commandStart, kind, payload);
        }
    }

    public override void Parse()
    {
        AddFourBytes("Hydrated data address (relative)");
        if (HeaderSize == 8)
        {
            AddFourBytes("Command stream end offset");
        }

        var commands = new Node { Start = LastChildEnd, Length = CommandsLength, Text = "Commands" };
        var bytes = Buffer.ReadBytes(commands.Start, commands.Length);
        Add(commands);

        const int pointerSize = 8;
        long copied = 0;
        long zeroFilled = 0;
        long relocations = 0;
        long hydrated = 0;
        foreach (var (offset, length, kind, payload) in Decode(bytes))
        {
            string text;
            switch (kind)
            {
                case 0:
                    copied += payload;
                    hydrated += payload;
                    text = $"Copy {payload} bytes";
                    break;
                case 1:
                    zeroFilled += payload;
                    hydrated += payload;
                    text = $"Zero fill {payload} bytes";
                    break;
                case 2:
                    relocations++;
                    hydrated += 4;
                    text = $"Relative pointer to fixup {payload}";
                    break;
                case 3:
                    relocations++;
                    hydrated += pointerSize;
                    text = $"Pointer to fixup {payload}";
                    break;
                case 4:
                    relocations += payload;
                    hydrated += payload * 4L;
                    text = $"{payload} inline relative pointers";
                    break;
                case 5:
                    relocations += payload;
                    hydrated += (long)payload * pointerSize;
                    text = $"{payload} inline pointers";
                    break;
                default:
                    text = $"Command {kind}";
                    break;
            }

            commands.Add(new Node { Start = commands.Start + offset, Length = length, Text = text });
        }

        int fixupCount = Remaining / 4;
        if (fixupCount > 0)
        {
            var fixups = new RecordTable
            {
                Start = LastChildEnd,
                Length = fixupCount * 4,
                Count = fixupCount,
                Fields = [("Relative pointer", 4)],
                RecordText = "Fixup"
            };
            Add(fixups);
            fixups.Text = $"Fixups ({fixupCount})";
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Trailing bytes");
        }

        HydratedSize = hydrated;
        Text = $"NativeAOT DehydratedData: {hydrated:N0} bytes in memory ({copied:N0} copied, {zeroFilled:N0} zero filled, {relocations:N0} relocations)";
    }

    public long HydratedSize { get; set; }
}

/// <summary>
/// Objects that exist before the program starts, such as string literals.
/// Each is an object header, a MethodTable pointer and the fields. The size
/// is read from the MethodTable: base size plus component size times length.
/// </summary>
public class FrozenObjectRegion : Node
{
    public PEFile PEFile { get; set; }
    public int ObjectCount { get; set; }
    public HashSet<int> MethodTableOffsets { get; } = new();

    public override void Parse()
    {
        int pointerSize = PEFile.PointerSize;
        int minimumSize = 3 * pointerSize;
        const uint HasComponentSize = 0x80000000;

        while (Remaining >= minimumSize)
        {
            int position = LastChildEnd;
            int methodTable = PEFile.ResolvePointerAt(position + pointerSize);
            if (methodTable <= 0 || methodTable + 8 > Buffer.Length)
            {
                break;
            }

            uint flags = Buffer.ReadUInt32(methodTable);
            uint baseSize = Buffer.ReadUInt32(methodTable + 4);
            if (baseSize < 2 * pointerSize || baseSize > 0x100000)
            {
                break;
            }

            MethodTableOffsets.Add(methodTable);

            long size = baseSize;
            int componentSize = (flags & HasComponentSize) != 0 ? (int)(flags & 0xFFFF) : 0;
            int count = 0;
            if (componentSize != 0)
            {
                count = Buffer.ReadInt32(position + 2 * pointerSize);
                if (count < 0)
                {
                    break;
                }

                size += (long)componentSize * count;
            }

            size = (size + pointerSize - 1) & ~(long)(pointerSize - 1);
            if (size > Remaining)
            {
                break;
            }

            var fields = new List<(string, int)>
            {
                ("Object header", pointerSize),
                ("MethodTable pointer", pointerSize)
            };
            int dataSize = (int)size - 2 * pointerSize;
            if (componentSize != 0)
            {
                fields.Add(("Length", 4));
                dataSize -= 4;
            }

            if (dataSize > 0)
            {
                fields.Add((componentSize != 0 ? "Elements" : "Fields", dataSize));
            }

            var frozenObject = new Record { Fields = fields.ToArray() };
            Add(frozenObject);
            ObjectCount++;

            bool isString = componentSize == 2 && baseSize == 2 * pointerSize + 4 + 2;
            if (isString)
            {
                var characters = Buffer.ReadBytes(position + 2 * pointerSize + 4, Math.Min(count, 200) * 2);
                frozenObject.Text = $"String \"{System.Text.Encoding.Unicode.GetString(characters)}\"";
            }
            else if (componentSize != 0)
            {
                frozenObject.Text = $"Array ({count} elements of {componentSize} bytes)";
            }
            else
            {
                frozenObject.Text = $"Object ({size} bytes)";
            }
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Remaining bytes");
        }
    }
}

public class NativeAotUnwindData : Node
{
    public byte Flags { get; set; }

    public override void Parse()
    {
        const int HasEHInfo = 0x04;
        const int IsReversePInvoke = 0x08;
        const int HasAssociatedData = 0x10;

        string kind = (Flags & 3) switch
        {
            0 => "method body",
            1 => "handler funclet",
            _ => "filter funclet"
        };
        var flagNames = new List<string> { kind };
        if ((Flags & HasEHInfo) != 0)
        {
            flagNames.Add("EH info");
        }

        if ((Flags & IsReversePInvoke) != 0)
        {
            flagNames.Add("reverse P/Invoke");
        }

        if ((Flags & HasAssociatedData) != 0)
        {
            flagNames.Add("associated data");
        }

        AddOneByte($"Flags: {string.Join(", ", flagNames)}");
        if ((Flags & HasAssociatedData) != 0)
        {
            AddFourBytes("Associated data (relative)");
        }

        if ((Flags & HasEHInfo) != 0)
        {
            AddFourBytes("EH info RVA");
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, (Flags & 3) == 0 ? "GC info" : "Funclet data");
        }

        Text = $"Managed unwind data ({kind})";
    }
}
