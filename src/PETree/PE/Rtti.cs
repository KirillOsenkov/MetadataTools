using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    /// <summary>
    /// Finds the run-time type information the Microsoft C++ compiler emits:
    /// type descriptors (found by their ".?A" decorated names), complete
    /// object locators, class hierarchy descriptors, base class descriptors,
    /// and the virtual function tables that point to a locator.
    /// </summary>
    private void AddRtti()
    {
        if (CLIHeader != null && ReadyToRunHeader != null)
        {
            return;
        }

        const uint ImageScnCntCode = 0x00000020;
        int pointerSize = PointerSize;
        bool usesRvas = IsPE32Plus;
        var typeNamesByOffset = new Dictionary<int, string>();
        var dataSections = new List<(int raw, byte[] bytes)>();

        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            string name = sectionHeader.Name.Text;
            if (raw <= 0 || size < 32 || (long)raw + size > Buffer.Length ||
                ((uint)sectionHeader.Characteristics.Value & ImageScnCntCode) != 0 ||
                name == ".rsrc" || name == ".reloc" || name == ".pdata")
            {
                continue;
            }

            var bytes = Buffer.ReadBytes(raw, size);
            dataSections.Add((raw, bytes));

            // A type descriptor is two pointers followed by the decorated name
            for (int i = 2 * pointerSize; i + 6 < bytes.Length; i += pointerSize)
            {
                if (bytes[i] != '.' || bytes[i + 1] != '?' || bytes[i + 2] != 'A')
                {
                    continue;
                }

                int end = i;
                while (end < bytes.Length && bytes[end] >= 0x21 && bytes[end] <= 0x7E)
                {
                    end++;
                }

                if (end >= bytes.Length || bytes[end] != 0 || end - i < 5)
                {
                    continue;
                }

                int descriptorOffset = raw + i - 2 * pointerSize;
                if (ResolvePointerAt(descriptorOffset) <= 0)
                {
                    continue;
                }

                string decorated = Encoding.ASCII.GetString(bytes, i, end - i);
                int length = 2 * pointerSize + (end - i) + 1;
                var descriptor = new Record
                {
                    Start = descriptorOffset,
                    Length = length,
                    Fields = [("type_info vftable pointer", pointerSize), ("Spare", pointerSize), ("Decorated name", length - 2 * pointerSize)]
                };
                if (TryAdd(descriptor))
                {
                    string typeName = UndecorateTypeName(decorated);
                    descriptor.Text = $"RTTI type descriptor: {typeName}";
                    typeNamesByOffset[descriptorOffset] = typeName;
                }
            }
        }

        if (typeNamesByOffset.Count == 0)
        {
            return;
        }

        int ResolveReference(int offset)
        {
            if (usesRvas)
            {
                int rva = Buffer.ReadInt32(offset);
                return rva > 0 ? ResolveVirtualAddress(rva) : 0;
            }

            return ResolvePointer(Buffer.ReadUInt32(offset));
        }

        var locatorNamesByOffset = new Dictionary<int, string>();
        var hierarchyOffsets = new HashSet<int>();
        int locatorSize = usesRvas ? 24 : 20;

        foreach (var (raw, bytes) in dataSections)
        {
            for (int i = 0; i + locatorSize <= bytes.Length; i += 4)
            {
                uint signature = BitConverter.ToUInt32(bytes, i);
                if (signature != (usesRvas ? 1u : 0u))
                {
                    continue;
                }

                int offset = raw + i;
                if (usesRvas && BitConverter.ToInt32(bytes, i + 20) != OffsetToRva(offset))
                {
                    continue;
                }

                int typeDescriptor = ResolveReference(offset + 12);
                if (typeDescriptor <= 0 || !typeNamesByOffset.TryGetValue(typeDescriptor, out var typeName))
                {
                    continue;
                }

                int hierarchy = ResolveReference(offset + 16);
                if (hierarchy <= 0 || hierarchy + 16 > Buffer.Length || Buffer.ReadUInt32(hierarchy) != 0)
                {
                    continue;
                }

                var fields = new List<(string, int)>
                {
                    ("Signature", 4),
                    ("Offset of vftable in class", 4),
                    ("Constructor displacement offset", 4),
                    ("Type descriptor", 4),
                    ("Class hierarchy descriptor", 4)
                };
                if (usesRvas)
                {
                    fields.Add(("Self RVA", 4));
                }

                var locator = new Record { Start = offset, Length = locatorSize, Fields = fields.ToArray() };
                if (!TryAdd(locator))
                {
                    continue;
                }

                locator.Text = $"RTTI complete object locator: {typeName}";
                locatorNamesByOffset[offset] = typeName;
                if (hierarchyOffsets.Add(hierarchy))
                {
                    AddRttiClassHierarchy(hierarchy, typeName, ResolveReference, typeNamesByOffset);
                }
            }
        }

        AddVftables(locatorNamesByOffset, dataSections);
    }

    private void AddRttiClassHierarchy(int offset, string typeName, Func<int, int> resolveReference, Dictionary<int, string> typeNamesByOffset)
    {
        var hierarchy = new Record
        {
            Start = offset,
            Length = 16,
            Fields = [("Signature", 4), ("Attributes", 4), ("Number of base classes", 4), ("Base class array", 4)]
        };
        if (!TryAdd(hierarchy))
        {
            return;
        }

        hierarchy.Text = $"RTTI class hierarchy descriptor: {typeName}";
        int count = hierarchy.ReadInt32(2);
        int arrayOffset = resolveReference(offset + 12);
        if (count <= 0 || count > 4096 || arrayOffset <= 0)
        {
            return;
        }

        var array = new RecordTable
        {
            Start = arrayOffset,
            Length = count * 4,
            Count = count,
            Fields = [("Base class descriptor", 4)]
        };
        if (!TryAdd(array))
        {
            return;
        }

        array.Text = $"RTTI base class array: {typeName} ({count})";
        for (int i = 0; i < count; i++)
        {
            int descriptorOffset = resolveReference(arrayOffset + i * 4);
            if (descriptorOffset <= 0 || !IsRangeAvailable(descriptorOffset, 28))
            {
                continue;
            }

            int baseType = resolveReference(descriptorOffset);
            string baseName = baseType > 0 && typeNamesByOffset.TryGetValue(baseType, out var name) ? name : "?";
            var descriptor = new Record
            {
                Start = descriptorOffset,
                Length = 28,
                Fields =
                [
                    ("Type descriptor", 4),
                    ("Number of contained bases", 4),
                    ("Member displacement", 4),
                    ("Vbtable displacement", 4),
                    ("Displacement inside vbtable", 4),
                    ("Attributes", 4),
                    ("Class hierarchy descriptor", 4)
                ]
            };
            Add(descriptor);
            descriptor.Text = $"RTTI base class descriptor: {baseName}";
        }
    }

    /// <summary>
    /// The slot in front of a virtual function table holds the address of
    /// the complete object locator of its class.
    /// </summary>
    private void AddVftables(Dictionary<int, string> locatorNamesByOffset, List<(int raw, byte[] bytes)> dataSections)
    {
        int pointerSize = PointerSize;
        var locatorSlots = new List<(int offset, string name)>();

        void Check(int slotOffset)
        {
            int target = ResolvePointerAt(slotOffset);
            if (target > 0 && locatorNamesByOffset.TryGetValue(target, out var name))
            {
                locatorSlots.Add((slotOffset, name));
            }
        }

        if (HasRelocations)
        {
            foreach (int slotOffset in relocationOffsets)
            {
                Check(slotOffset);
            }
        }
        else
        {
            foreach (var (raw, bytes) in dataSections)
            {
                for (int i = 0; i + pointerSize <= bytes.Length; i += pointerSize)
                {
                    Check(raw + i);
                }
            }
        }

        var slotSet = new HashSet<int>(locatorSlots.Select(s => s.offset));
        foreach (var (offset, name) in locatorSlots)
        {
            int count = 0;
            while (count < 4096)
            {
                int slot = offset + (count + 1) * pointerSize;
                if (slot + pointerSize > Buffer.Length || slotSet.Contains(slot) || (HasRelocations && !IsRelocated(slot)))
                {
                    break;
                }

                int rva = VirtualAddressToRva(ReadPointer(slot));
                if (rva == 0 || !IsCodeAddress(rva))
                {
                    break;
                }

                count++;
            }

            int length = (count + 1) * pointerSize;
            var fields = new (string, int)[count + 1];
            fields[0] = ("Complete object locator pointer", pointerSize);
            for (int i = 0; i < count; i++)
            {
                fields[i + 1] = ($"Virtual function {i}", pointerSize);
            }

            var vftable = new Record { Start = offset, Length = length, Fields = fields };
            if (TryAdd(vftable))
            {
                vftable.Text = $"Vftable: {name} ({count} functions)";
            }
        }
    }

    /// <summary>
    /// Turns a decorated type name such as ".?AVfoo@bar@@" into "class
    /// bar::foo". Names with templates or other encodings are returned as is.
    /// </summary>
    public static string UndecorateTypeName(string decorated)
    {
        if (decorated.Length < 6 || !decorated.StartsWith(".?A") || !decorated.EndsWith("@@"))
        {
            return decorated;
        }

        string kind;
        int nameStart;
        switch (decorated[3])
        {
            case 'V':
                kind = "class";
                nameStart = 4;
                break;
            case 'U':
                kind = "struct";
                nameStart = 4;
                break;
            case 'T':
                kind = "union";
                nameStart = 4;
                break;
            case 'W':
                kind = "enum";
                nameStart = 5;
                break;
            default:
                return decorated;
        }

        string body = decorated.Substring(nameStart, decorated.Length - nameStart - 2);
        if (body.Length == 0 || body.IndexOf('?') >= 0 || body.IndexOf('$') >= 0)
        {
            return decorated;
        }

        var parts = body.Split('@');
        Array.Reverse(parts);
        return $"{kind} {string.Join("::", parts)}";
    }
}
