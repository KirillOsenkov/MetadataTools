using System;
using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    /// <summary>
    /// A 32-bit x86 image has no function table. Its exception data is
    /// referenced only from code, so the data sections are searched for the
    /// C++ FuncInfo magic numbers and for the shape of the scope tables of
    /// _except_handler4 and _except_handler3.
    /// </summary>
    private void AddX86ExceptionData()
    {
        if (Machine != MachineI386 || ReadyToRunHeader != null || NativeImageHeader != null)
        {
            return;
        }

        const uint ImageScnCntCode = 0x00000020;
        bool usesExceptHandler3 = ImportNamesBySlotRva.Values.Any(name => name.EndsWith("_except_handler3"));

        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            int raw = sectionHeader.PointerToRawData.Value;
            int size = sectionHeader.SizeOfRawData.Value;
            string name = sectionHeader.Name.Text;
            if (raw <= 0 || size < 28 || (long)raw + size > Buffer.Length ||
                ((uint)sectionHeader.Characteristics.Value & ImageScnCntCode) != 0 ||
                name == ".rsrc" || name == ".reloc")
            {
                continue;
            }

            var bytes = Buffer.ReadBytes(raw, size);
            for (int i = 0; i + 28 <= bytes.Length; i += 4)
            {
                uint value = BitConverter.ToUInt32(bytes, i);
                if (value >= 0x19930520 && value <= 0x19930522)
                {
                    AddX86FuncInfo(raw + i, value);
                }
                else if (value == 0xFFFFFFFE || (value >= 0xFFFFFF00 && (value & 3) == 0))
                {
                    AddScopeTable4(raw + i, bytes, i);
                }
                else if (value == 0xFFFFFFFF && usesExceptHandler3)
                {
                    AddScopeTable3(raw + i);
                }
            }
        }
    }

    /// <summary>
    /// True when the slot holds the address of code. In an image with
    /// relocations the slot also has to be listed there.
    /// </summary>
    private bool IsCodePointerSlot(int offset)
    {
        if (offset + 4 > Buffer.Length || (HasRelocations && !IsRelocated(offset)))
        {
            return false;
        }

        int rva = VirtualAddressToRva(Buffer.ReadUInt32(offset));
        return rva != 0 && IsCodeAddress(rva);
    }

    private bool IsDataPointerSlot(int offset)
    {
        if (offset + 4 > Buffer.Length || (HasRelocations && !IsRelocated(offset)))
        {
            return false;
        }

        int rva = VirtualAddressToRva(Buffer.ReadUInt32(offset));
        return rva != 0 && !IsCodeAddress(rva) && ResolveVirtualAddress(rva) > 0;
    }

    private void AddX86FuncInfo(int offset, uint magic)
    {
        int size = magic == 0x19930520 ? 28 : magic == 0x19930521 ? 32 : 36;
        if (offset + size > Buffer.Length)
        {
            return;
        }

        int maxState = Buffer.ReadInt32(offset + 4);
        int tryBlockCount = Buffer.ReadInt32(offset + 12);
        int ipMapCount = Buffer.ReadInt32(offset + 20);
        if (maxState < 0 || maxState > 0x10000 || tryBlockCount < 0 || tryBlockCount > 0x1000 || ipMapCount < 0 || ipMapCount > 0x10000)
        {
            return;
        }

        // A table pointer is zero exactly when its count is zero
        if ((maxState > 0) != IsDataPointerSlot(offset + 8) || (maxState == 0 && Buffer.ReadUInt32(offset + 8) != 0) ||
            (tryBlockCount > 0) != IsDataPointerSlot(offset + 16) || (tryBlockCount == 0 && Buffer.ReadUInt32(offset + 16) != 0) ||
            (maxState == 0 && tryBlockCount == 0))
        {
            return;
        }

        var fields = new List<(string, int)>
        {
            ("Magic number", 4),
            ("Max state", 4),
            ("Unwind map address", 4),
            ("Try block count", 4),
            ("Try block map address", 4),
            ("IP map entry count", 4),
            ("IP to state map address", 4)
        };
        if (size >= 32)
        {
            fields.Add(("ES type list address", 4));
        }

        if (size >= 36)
        {
            fields.Add(("EH flags", 4));
        }

        var funcInfo = new Record { Start = offset, Length = size, Fields = fields.ToArray() };
        if (!TryAdd(funcInfo))
        {
            return;
        }

        funcInfo.Text = "C++ FuncInfo";
        AddX86Table(ResolvePointerAt(offset + 8), maxState, "C++ unwind map", "Unwind map entry", [("To state", 4), ("Action address", 4)]);
        var tryBlocks = AddX86Table(
            ResolvePointerAt(offset + 16),
            tryBlockCount,
            "C++ try block map",
            "Try block",
            [("Try low", 4), ("Try high", 4), ("Catch high", 4), ("Catch count", 4), ("Handler array address", 4)]);
        if (tryBlocks == null)
        {
            return;
        }

        foreach (var tryBlock in tryBlocks.Children.OfType<Record>())
        {
            AddX86Table(
                ResolvePointerAt(tryBlock.Children[4].Start),
                tryBlock.ReadInt32(3),
                "C++ catch handlers",
                "Handler type",
                [("Adjectives", 4), ("Type descriptor address", 4), ("Catch object offset", 4), ("Handler address", 4)]);
        }
    }

    private RecordTable AddX86Table(int offset, int count, string text, string recordText, (string, int)[] fields)
    {
        int recordSize = fields.Sum(f => f.Item2);
        if (offset <= 0 || count <= 0 || count > 0x10000 || !cxxDataOffsets.Add(offset))
        {
            return null;
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = count * recordSize,
            Count = count,
            Fields = fields,
            RecordText = recordText
        };
        if (!TryAdd(table))
        {
            return null;
        }

        table.Text = $"{text} ({count})";
        return table;
    }

    /// <summary>
    /// _EH4_SCOPETABLE: four cookie offsets, then one record per __try level
    /// with the enclosing level, the filter and the handler.
    /// </summary>
    private void AddScopeTable4(int offset, byte[] bytes, int index)
    {
        if (index + 28 > bytes.Length ||
            BitConverter.ToUInt32(bytes, index + 4) != 0 ||
            BitConverter.ToUInt32(bytes, index + 8) < 0xFFFFF000 ||
            BitConverter.ToUInt32(bytes, index + 12) != 0 ||
            BitConverter.ToUInt32(bytes, index + 16) != 0xFFFFFFFE)
        {
            return;
        }

        int count = CountScopeRecords(offset + 16, topLevel: 0xFFFFFFFE);
        if (count == 0)
        {
            return;
        }

        var table = new ScopeTable4 { Start = offset, Length = 16 + count * 12, RecordCount = count };
        TryAdd(table);
    }

    private void AddScopeTable3(int offset)
    {
        int count = CountScopeRecords(offset, topLevel: 0xFFFFFFFF);
        if (count == 0)
        {
            return;
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = count * 12,
            Count = count,
            Fields = [("Enclosing level", 4), ("Filter address", 4), ("Handler address", 4)],
            RecordText = "Scope record"
        };
        if (TryAdd(table))
        {
            table.Text = $"SEH scope table of _except_handler3 ({count})";
        }
    }

    private int CountScopeRecords(int offset, uint topLevel)
    {
        int count = 0;
        while (count < 256 && offset + (count + 1) * 12 <= Buffer.Length)
        {
            int record = offset + count * 12;
            uint enclosingLevel = Buffer.ReadUInt32(record);
            bool isLevelValid = enclosingLevel == topLevel || enclosingLevel < (uint)count;
            bool isFilterValid = Buffer.ReadUInt32(record + 4) == 0 || IsCodePointerSlot(record + 4);
            if (!isLevelValid || !isFilterValid || !IsCodePointerSlot(record + 8))
            {
                break;
            }

            count++;
        }

        return count;
    }
}

public class ScopeTable4 : Node
{
    public int RecordCount { get; set; }

    public override void Parse()
    {
        AddFourBytes("GS cookie offset");
        AddFourBytes("GS cookie XOR offset");
        AddFourBytes("EH cookie offset");
        AddFourBytes("EH cookie XOR offset");
        for (int i = 0; i < RecordCount; i++)
        {
            Add(new Record
            {
                Fields = [("Enclosing level", 4), ("Filter address", 4), ("Handler address", 4)],
                Text = "Scope record"
            });
        }

        Text = $"SEH scope table of _except_handler4 ({RecordCount})";
    }
}
