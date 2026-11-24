using System;
using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    private const int MethodTableFixedSize = 24;
    private const int TypeManagerIndirectionSection = 204;

    private const uint MethodTableKindMask = 0x00030000;
    private const uint MethodTableKindFunctionPointer = 0x00010000;
    private const uint MethodTableKindParameterized = 0x00020000;
    private const uint MethodTableKindGenericDefinition = 0x00030000;
    private const uint MethodTableHasDispatchMap = 0x00040000;
    private const uint MethodTableIsDynamic = 0x00080000;
    private const uint MethodTableHasFinalizer = 0x00100000;
    private const uint MethodTableHasSealedVTable = 0x00400000;
    private const uint MethodTableHasVariance = 0x00800000;
    private const uint MethodTableHasPointers = 0x01000000;
    private const uint MethodTableIsGeneric = 0x02000000;
    private const uint MethodTableHasComponentSize = 0x80000000;

    /// <summary>
    /// Labels MethodTables, the type descriptors of the NativeAOT runtime, and
    /// the data each one points to: the GC descriptor in front of it, its
    /// interface dispatch map, sealed vtable, generic composition and
    /// writable data.
    ///
    /// The fixed part of a MethodTable, its vtable and its interface list are
    /// followed by a pointer to the type manager cell, which the module
    /// header locates. That makes a MethodTable recognizable: a position is
    /// one when the field at the offset that its two slot counts give points
    /// to that cell.
    /// </summary>
    private void AddNativeAotMethodTables()
    {
        if (!IsNativeAot || PointerSize != 8)
        {
            return;
        }

        int typeManagerRva = 0;
        foreach (var header in NativeAotModuleHeaders)
        {
            foreach (var row in header.Rows)
            {
                if (row.ReadInt32(0) == TypeManagerIndirectionSection)
                {
                    typeManagerRva = VirtualAddressToRva(ReadPointer(row.Children[2].Start));
                }
            }
        }

        var methodTables = typeManagerRva != 0 ? ScanMethodTables(typeManagerRva) : null;
        if (methodTables == null || methodTables.Count == 0)
        {
            AddNativeAotMethodTablesFromReferences();
            return;
        }

        AddBatch(methodTables.Cast<Node>().ToList());
        AddMethodTableData(methodTables.Where(m => m.Parent != null).ToList());
    }

    private List<MethodTable> ScanMethodTables(int typeManagerRva)
    {
        const uint ImageScnCntCode = 0x00000020;
        ulong typeManagerAddress = ImageBase + (uint)typeManagerRva;
        long sizeOfImage = SizeOfImage;
        var result = new List<MethodTable>();

        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            if (raw <= 0 || size < MethodTableFixedSize + 8 || (long)raw + size > Buffer.Length ||
                ((uint)sectionHeader.Characteristics.Value & ImageScnCntCode) != 0)
            {
                continue;
            }

            int sectionRva = sectionHeader.VirtualAddress.Value;
            var bytes = Buffer.ReadBytes(raw, size);
            int claimedEnd = 0;
            for (int i = (8 - (raw & 7)) & 7; i + MethodTableFixedSize + 8 <= size; i += 8)
            {
                if (i < claimedEnd)
                {
                    continue;
                }

                int vtableSlots = BitConverter.ToUInt16(bytes, i + 16);
                int interfaces = BitConverter.ToUInt16(bytes, i + 18);
                int slotsEnd = i + MethodTableFixedSize + 8 * (vtableSlots + interfaces);
                if (slotsEnd + 8 > size)
                {
                    continue;
                }

                // The pointer to the type manager cell is relative where the
                // image uses relative pointers, a full pointer otherwise
                bool isRelative = sectionRva + slotsEnd + BitConverter.ToInt32(bytes, slotsEnd) == typeManagerRva;
                if (!isRelative && (BitConverter.ToUInt64(bytes, slotsEnd) != typeManagerAddress || !IsRelocated(raw + slotsEnd)))
                {
                    continue;
                }

                uint flags = BitConverter.ToUInt32(bytes, i);
                if ((flags & MethodTableIsDynamic) != 0 || !IsPointerSlot(bytes, raw, i + 8))
                {
                    continue;
                }

                bool areSlotsValid = true;
                for (int slot = i + MethodTableFixedSize; slot < slotsEnd && areSlotsValid; slot += 8)
                {
                    areSlotsValid = IsPointerSlot(bytes, raw, slot);
                }

                if (!areSlotsValid)
                {
                    continue;
                }

                uint baseSize = BitConverter.ToUInt32(bytes, i + 4);
                uint kind = flags & MethodTableKindMask;
                bool isGeneric = (flags & MethodTableIsGeneric) != 0;
                int pointerFieldSize = isRelative ? 4 : 8;
                var optionalFields = new List<string> { "Type manager indirection", "Writable data" };
                if ((flags & MethodTableHasDispatchMap) != 0)
                {
                    optionalFields.Add("Dispatch map");
                }

                if ((flags & MethodTableHasFinalizer) != 0)
                {
                    optionalFields.Add("Finalizer");
                }

                if ((flags & MethodTableHasSealedVTable) != 0)
                {
                    optionalFields.Add("Sealed vtable");
                }

                if (isGeneric)
                {
                    optionalFields.Add("Generic definition");
                }

                if (isGeneric || (kind == MethodTableKindGenericDefinition && (flags & MethodTableHasVariance) != 0))
                {
                    optionalFields.Add(isGeneric ? "Generic composition" : "Generic variance");
                }

                if (kind == MethodTableKindFunctionPointer)
                {
                    uint parameterCount = baseSize & 0x7FFFFFFF;
                    for (int p = 0; p < parameterCount && p < 256; p++)
                    {
                        optionalFields.Add($"Function pointer parameter {p}");
                    }
                }

                // The fields after the first two depend on the flags. When
                // one of them does not point into the image the flags have a
                // meaning this reader does not have, and only the first two
                // are kept.
                if (isRelative)
                {
                    for (int f = 2; f < optionalFields.Count; f++)
                    {
                        int field = slotsEnd + f * 4;
                        long target = field + 4 <= size ? (long)sectionRva + field + BitConverter.ToInt32(bytes, field) : -1;
                        if (target <= 0 || target >= sizeOfImage)
                        {
                            optionalFields.RemoveRange(2, optionalFields.Count - 2);
                            break;
                        }
                    }
                }

                int length = slotsEnd - i + optionalFields.Count * pointerFieldSize;
                if (i + length > size)
                {
                    continue;
                }

                result.Add(new MethodTable
                {
                    Start = raw + i,
                    Length = length,
                    Flags = flags,
                    BaseSize = baseSize,
                    VtableSlotCount = vtableSlots,
                    InterfaceCount = interfaces,
                    OptionalFields = optionalFields,
                    PointerFieldSize = pointerFieldSize
                });
                claimedEnd = i + length;
            }
        }

        return result;
    }

    /// <summary>
    /// True when the 8 bytes are zero or a pointer that the relocation table lists.
    /// </summary>
    private bool IsPointerSlot(byte[] bytes, int raw, int index)
    {
        ulong value = BitConverter.ToUInt64(bytes, index);
        if (value == 0)
        {
            return true;
        }

        return HasRelocations ? IsRelocated(raw + index) : VirtualAddressToRva(value) != 0;
    }

    /// <summary>
    /// Adds nodes that have Start and Length set. A node is skipped when its
    /// range is not free or overlaps a node earlier in the list. Unlike
    /// TryAdd this merges the nodes into the child lists once, which matters
    /// for tens of thousands of nodes.
    /// </summary>
    private void AddBatch(List<Node> nodes)
    {
        nodes.Sort((left, right) => left.Start.CompareTo(right.Start));
        var groups = new Dictionary<Node, List<Node>>();
        int previousEnd = 0;
        foreach (var node in nodes)
        {
            if (node.Start < previousEnd || node.Length <= 0)
            {
                continue;
            }

            var parent = FindAvailableParent(node.Start, node.Length);
            if (parent == null)
            {
                continue;
            }

            if (!groups.TryGetValue(parent, out var group))
            {
                group = new List<Node>();
                groups[parent] = group;
            }

            group.Add(node);
            previousEnd = node.End;
        }

        foreach (var pair in groups)
        {
            pair.Key.AddBetweenChildren(pair.Value);
        }
    }

    private int ResolveRelativePointer(int offset)
    {
        int value = Buffer.ReadInt32(offset);
        int rva = OffsetToRva(offset);
        return value == 0 || rva == 0 ? 0 : ResolveVirtualAddress(rva + value);
    }

    private void AddMethodTableData(List<MethodTable> methodTables)
    {
        var nodes = new List<Node>();
        var seen = new HashSet<int>();
        var sealedVTables = new List<int>();

        foreach (var methodTable in methodTables)
        {
            AddGcDescriptor(methodTable, nodes);

            // A vtable slot that points to data is the generic dictionary of
            // the type that the slice of the vtable belongs to
            for (int i = 0; i < methodTable.VtableSlotCount; i++)
            {
                int slot = methodTable.Start + MethodTableFixedSize + i * 8;
                ulong value = Buffer.ReadUInt64(slot);
                int rva = value == 0 ? 0 : VirtualAddressToRva(value);
                if (rva != 0 && !IsCodeAddress(rva))
                {
                    int dictionary = ResolveVirtualAddress(rva);
                    if (dictionary > 0)
                    {
                        PointerTableNames[dictionary] = "Type generic dictionary";
                    }
                }
            }

            foreach (var field in methodTable.OptionalFieldNodes)
            {
                int target = methodTable.PointerFieldSize == 4 ? ResolveRelativePointer(field.Start) : ResolvePointerAt(field.Start);
                if (target <= 0 || !seen.Add(target))
                {
                    continue;
                }

                switch (field.Text)
                {
                    case "Writable data":
                        nodes.Add(new Node { Start = target, Length = 8, Text = "Writable data of a MethodTable" });
                        break;
                    case "Dispatch map":
                        AddDispatchMap(target, nodes);
                        break;
                    case "Sealed vtable":
                        sealedVTables.Add(target);
                        break;
                    case "Generic composition":
                        AddGenericComposition(methodTable, target, nodes);
                        break;
                }
            }
        }

        AddBatch(nodes);
        AddSealedVTables(sealedVTables);
    }

    /// <summary>
    /// A type with object references has a GC descriptor in front of its
    /// MethodTable: the series count right before the MethodTable and the
    /// series before that.
    /// </summary>
    private void AddGcDescriptor(MethodTable methodTable, List<Node> nodes)
    {
        if ((methodTable.Flags & MethodTableHasPointers) == 0 || methodTable.Start < 16)
        {
            return;
        }

        long seriesCount = (long)Buffer.ReadUInt64(methodTable.Start - 8);
        if (seriesCount == 0 || seriesCount > 4096 || seriesCount < -4096)
        {
            return;
        }

        var fields = new List<(string, int)>();
        if (seriesCount > 0)
        {
            for (long i = 0; i < seriesCount; i++)
            {
                fields.Add(("Series size", 8));
                fields.Add(("Series start offset", 8));
            }
        }
        else
        {
            // An array of structs with references: one item per run of
            // references in the element, then the offset of the first element
            for (long i = 0; i < -seriesCount; i++)
            {
                fields.Add(("Reference count", 4));
                fields.Add(("Bytes to skip", 4));
            }

            fields.Add(("Start offset", 8));
        }

        fields.Add(("Series count", 8));
        int size = fields.Sum(f => f.Item2);
        if (size > methodTable.Start)
        {
            return;
        }

        nodes.Add(new Record
        {
            Start = methodTable.Start - size,
            Length = size,
            Fields = fields.ToArray(),
            Text = $"GC descriptor ({Math.Abs(seriesCount)} series)"
        });
    }

    private void AddDispatchMap(int offset, List<Node> nodes)
    {
        if (offset + 8 > Buffer.Length)
        {
            return;
        }

        int standard = Buffer.ReadUInt16(offset);
        int defaults = Buffer.ReadUInt16(offset + 2);
        int standardStatic = Buffer.ReadUInt16(offset + 4);
        int defaultStatic = Buffer.ReadUInt16(offset + 6);
        var fields = new List<(string, int)>
        {
            ("Entry count", 2),
            ("Default implementation entry count", 2),
            ("Static entry count", 2),
            ("Static default implementation entry count", 2)
        };
        for (int i = 0; i < standard + defaults; i++)
        {
            fields.Add(("Interface index", 2));
            fields.Add(("Interface method slot", 2));
            fields.Add(("Implementation method slot", 2));
        }

        for (int i = 0; i < standardStatic + defaultStatic; i++)
        {
            fields.Add(("Interface index", 2));
            fields.Add(("Interface method slot", 2));
            fields.Add(("Implementation method slot", 2));
            fields.Add(("Context source", 2));
        }

        int size = fields.Sum(f => f.Item2);
        if ((long)offset + size > Buffer.Length)
        {
            return;
        }

        nodes.Add(new Record
        {
            Start = offset,
            Length = size,
            Fields = fields.ToArray(),
            Text = $"Interface dispatch map ({standard + defaults + standardStatic + defaultStatic} entries)"
        });
    }

    /// <summary>
    /// The type arguments of a generic instance. With one argument the field
    /// of the MethodTable points to the argument itself; with more it points
    /// to a list of relative pointers.
    /// </summary>
    private void AddGenericComposition(MethodTable methodTable, int target, List<Node> nodes)
    {
        var definitionField = methodTable.OptionalFieldNodes.FirstOrDefault(f => f.Text == "Generic definition");
        if (definitionField == null || methodTable.PointerFieldSize != 4)
        {
            return;
        }

        int definition = ResolveRelativePointer(definitionField.Start);
        if (definition <= 0 || definition + MethodTableFixedSize > Buffer.Length ||
            (Buffer.ReadUInt32(definition) & MethodTableKindMask) != MethodTableKindGenericDefinition)
        {
            return;
        }

        int arity = Buffer.ReadUInt16(definition + 4);
        if (arity < 2 || arity > 1024)
        {
            return;
        }

        var table = new RecordTable
        {
            Start = target,
            Length = arity * 4,
            Count = arity,
            Fields = [("Type argument (relative pointer)", 4)],
            Text = $"Generic composition ({arity} type arguments)"
        };
        nodes.Add(table);
    }

    /// <summary>
    /// A sealed vtable is a list of relative pointers to methods. Its length
    /// is not stored: it ends where the next one starts, or at the first
    /// entry that does not point to code.
    /// </summary>
    private void AddSealedVTables(List<int> starts)
    {
        const int MaximumLength = 0x4000;
        starts.Sort();
        var nodes = new List<Node>();
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i];
            int limit = Math.Min(MaximumLength, GetFreeLength(start, MaximumLength));
            if (i + 1 < starts.Count)
            {
                limit = Math.Min(limit, starts[i + 1] - start);
            }

            int count = 0;
            while ((count + 1) * 4 <= limit)
            {
                int slot = start + count * 4;
                int value = Buffer.ReadInt32(slot);
                int rva = OffsetToRva(slot) + value;
                if (value == 0 || rva <= 0 || !IsCodeAddress(rva))
                {
                    break;
                }

                count++;
            }

            if (count > 0)
            {
                nodes.Add(new RecordTable
                {
                    Start = start,
                    Length = count * 4,
                    Count = count,
                    Fields = [("Method (relative pointer)", 4)],
                    Text = $"Sealed vtable ({count} slots)"
                });
            }
        }

        AddBatch(nodes);
    }
}

/// <summary>
/// The type descriptor of the NativeAOT runtime: flags, base size, related
/// type, vtable, interface list, then pointers to further data of the type.
/// </summary>
public class MethodTable : Node
{
    public uint Flags { get; set; }
    public uint BaseSize { get; set; }
    public int VtableSlotCount { get; set; }
    public int InterfaceCount { get; set; }
    public int PointerFieldSize { get; set; }
    public List<string> OptionalFields { get; set; }
    public List<Node> OptionalFieldNodes { get; } = new();

    private static readonly string[] elementTypeNames =
    [
        "Unknown", "Void", "Boolean", "Char", "SByte", "Byte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "IntPtr", "UIntPtr", "Single", "Double", "Struct", "Enum", "Nullable", "0x13", "Class", "Interface",
        "System.Array", "Array", "SzArray", "ByRef", "Pointer", "Function pointer"
    ];

    public string Kind
    {
        get
        {
            uint kind = Flags & 0x00030000;
            if (kind == 0x00030000)
            {
                return "Generic definition";
            }

            int elementType = (int)((Flags >> 26) & 0x1F);
            return elementType < elementTypeNames.Length ? elementTypeNames[elementType] : $"Element type {elementType}";
        }
    }

    public override void Parse()
    {
        AddFourBytes("Flags");
        AddFourBytes("Base size");
        AddEightBytes("Related type");
        AddTwoBytes("Vtable slot count");
        AddTwoBytes("Interface count");
        AddFourBytes("Hash code");
        for (int i = 0; i < VtableSlotCount; i++)
        {
            AddEightBytes($"Vtable slot {i}");
        }

        for (int i = 0; i < InterfaceCount; i++)
        {
            AddEightBytes($"Interface {i}");
        }

        foreach (string field in OptionalFields)
        {
            OptionalFieldNodes.Add(AddField(field, PointerFieldSize));
        }

        string generic = (Flags & 0x02000000) != 0 ? ", generic" : "";
        Text = $"MethodTable: {Kind}{generic}, base size {BaseSize}, {VtableSlotCount} vtable slots, {InterfaceCount} interfaces";
    }
}
