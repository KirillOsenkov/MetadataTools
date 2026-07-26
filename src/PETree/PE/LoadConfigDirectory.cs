namespace GuiLabs.FileFormat.PE;

public class LoadConfigDirectory : Node
{
    public LoadConfigDirectory()
    {
        Text = "Load config table";
    }

    public bool IsPE32Plus { get; set; }
    public PEFile PEFile { get; set; }

    public override void Parse()
    {
        int actualSize = Buffer.ReadInt32(Start);
        if (actualSize < 4 ||
            actualSize > Buffer.Length - Start ||
            Parent is Section section && actualSize > section.End - Start ||
            actualSize > Length &&
                (PEFile == null || !PEFile.IsRangeAvailable(End, actualSize - Length)))
        {
            return;
        }

        Length = actualSize;
        AddFourBytes("Size");

        AddNamedFields(actualSize);

        int remaining = actualSize - (LastChildEnd - Start);
        if (remaining > 0)
        {
            AddBytes(remaining, "Additional load config fields");
        }

        if (PEFile == null)
        {
            return;
        }

        ulong imageBase = IsPE32Plus
            ? Buffer.ReadUInt64(PEFile.OptionalHeader.WindowsFields.ImageBase.Start)
            : Buffer.ReadUInt32(PEFile.OptionalHeader.WindowsFields.ImageBase.Start);

        if (!IsPE32Plus)
        {
            TryAddTable(0x40, 0x44, actualSize, imageBase, 4, "Safe exception handler table");
        }

        int guardFlagsOffset = IsPE32Plus ? 0x90 : 0x58;
        if (actualSize < guardFlagsOffset + 4)
        {
            return;
        }

        uint guardFlags = Buffer.ReadUInt32(Start + guardFlagsOffset);
        int guardEntrySize = 4 + (int)(guardFlags >> 28);

        if (IsPE32Plus)
        {
            TryAddTable(0x80, 0x88, actualSize, imageBase, guardEntrySize, "Guard CF function table");
            TryAddTable(0xA0, 0xA8, actualSize, imageBase, guardEntrySize, "Guard CF IAT table");
            TryAddTable(0xB0, 0xB8, actualSize, imageBase, guardEntrySize, "Guard CF long jump table");
            TryAddTable(0x108, 0x110, actualSize, imageBase, guardEntrySize, "Guard EH continuation table");
        }
        else
        {
            TryAddTable(0x50, 0x54, actualSize, imageBase, guardEntrySize, "Guard CF function table");
            TryAddTable(0x68, 0x6C, actualSize, imageBase, guardEntrySize, "Guard CF IAT table");
            TryAddTable(0x70, 0x74, actualSize, imageBase, guardEntrySize, "Guard CF long jump table");
            TryAddTable(0xA4, 0xA8, actualSize, imageBase, guardEntrySize, "Guard EH continuation table");
        }

        AddDynamicValueRelocationTable(actualSize);
    }

    private void AddDynamicValueRelocationTable(int actualSize)
    {
        // The DVRT is located by a raw offset into the section given by a
        // 1-based section index (usually .reloc), not by RVA
        int offsetFieldOffset = IsPE32Plus ? 0xE0 : 0x88;
        int sectionFieldOffset = IsPE32Plus ? 0xE4 : 0x8C;
        if (actualSize < sectionFieldOffset + 2)
        {
            return;
        }

        int tableOffset = Buffer.ReadInt32(Start + offsetFieldOffset);
        int sectionIndex = Buffer.ReadUInt16(Start + sectionFieldOffset);
        var sections = PEFile.SectionTable.SectionHeaders;
        if (tableOffset <= 0 || sectionIndex <= 0 || sectionIndex > sections.Count)
        {
            return;
        }

        var section = sections[sectionIndex - 1];
        long fileOffset = (long)section.PointerToRawData.Value + tableOffset;
        if (fileOffset <= 0 || fileOffset + 8 > Buffer.Length)
        {
            return;
        }

        int version = Buffer.ReadInt32((int)fileOffset);
        int size = Buffer.ReadInt32((int)fileOffset + 4);
        if (version < 1 || version > 2 || size <= 0 || fileOffset + 8 + size > Buffer.Length)
        {
            return;
        }

        if (!PEFile.IsRangeAvailable((int)fileOffset, 8 + size))
        {
            return;
        }

        var table = new DynamicValueRelocationTable
        {
            Start = (int)fileOffset,
            Length = 8 + size,
            IsPE32Plus = IsPE32Plus
        };
        PEFile.Add(table);
    }

    private void AddNamedFields(int actualSize)
    {
        // (name, size) pairs in struct order; 0 stands for pointer-sized
        (string name, int size)[] fields =
        [
            ("TimeDate stamp", 4),
            ("Major version", 2),
            ("Minor version", 2),
            ("Global flags clear", 4),
            ("Global flags set", 4),
            ("Critical section default timeout", 4),
            ("DeCommit free block threshold", 0),
            ("DeCommit total free threshold", 0),
            ("Lock prefix table", 0),
            ("Maximum allocation size", 0),
            ("Virtual memory threshold", 0),
            IsPE32Plus ? ("Process affinity mask", 0) : ("Process heap flags", 4),
            IsPE32Plus ? ("Process heap flags", 4) : ("Process affinity mask", 4),
            ("CSD version", 2),
            ("Dependent load flags", 2),
            ("Edit list", 0),
            ("Security cookie", 0),
            ("SE handler table", 0),
            ("SE handler count", 0),
            ("Guard CF check function pointer", 0),
            ("Guard CF dispatch function pointer", 0),
            ("Guard CF function table", 0),
            ("Guard CF function count", 0),
            ("Guard flags", 4),
            ("Code integrity", 12),
            ("Guard address taken IAT entry table", 0),
            ("Guard address taken IAT entry count", 0),
            ("Guard long jump target table", 0),
            ("Guard long jump target count", 0),
            ("Dynamic value reloc table", 0),
            ("CHPE metadata pointer", 0),
            ("Guard RF failure routine", 0),
            ("Guard RF failure routine function pointer", 0),
            ("Dynamic value reloc table offset", 4),
            ("Dynamic value reloc table section", 2),
            ("Reserved2", 2),
            ("Guard RF verify stack pointer function pointer", 0),
            ("Hot patch table offset", 4),
            ("Reserved3", 4),
            ("Enclave configuration pointer", 0),
            ("Volatile metadata pointer", 0),
            ("Guard EH continuation table", 0),
            ("Guard EH continuation count", 0),
            ("Guard XFG check function pointer", 0),
            ("Guard XFG dispatch function pointer", 0),
            ("Guard XFG table dispatch function pointer", 0),
            ("CastGuard OS determined failure mode", 0),
            ("Guard memcpy function pointer", 0),
        ];

        int pointerSize = IsPE32Plus ? 8 : 4;
        foreach (var (name, size) in fields)
        {
            int fieldSize = size == 0 ? pointerSize : size;
            if (LastChildEnd - Start + fieldSize > actualSize)
            {
                break;
            }

            AddBytes(fieldSize, name);
        }
    }

    private void TryAddTable(
        int vaFieldOffset,
        int countFieldOffset,
        int actualSize,
        ulong imageBase,
        int entrySize,
        string name)
    {
        int pointerSize = IsPE32Plus ? 8 : 4;
        if (actualSize < vaFieldOffset + pointerSize ||
            actualSize < countFieldOffset + pointerSize)
        {
            return;
        }

        ulong tableVA = IsPE32Plus
            ? Buffer.ReadUInt64(Start + vaFieldOffset)
            : Buffer.ReadUInt32(Start + vaFieldOffset);
        ulong count = IsPE32Plus
            ? Buffer.ReadUInt64(Start + countFieldOffset)
            : Buffer.ReadUInt32(Start + countFieldOffset);

        if (tableVA < imageBase ||
            tableVA - imageBase > int.MaxValue ||
            count == 0 ||
            count > (ulong)(int.MaxValue / entrySize))
        {
            return;
        }

        int tableRva = (int)(tableVA - imageBase);
        int tableOffset = PEFile.ResolveVirtualAddress(tableRva);
        if (tableOffset <= 0)
        {
            return;
        }

        int totalSize = (int)count * entrySize;
        var section = PEFile.GetSectionAtVirtualAddress(tableRva);
        if (section == null ||
            totalSize > (long)section.PointerToRawData.Value + section.SizeOfRawData.Value - tableOffset ||
            !PEFile.IsRangeAvailable(tableOffset, totalSize))
        {
            return;
        }

        var table = new LoadConfigTable
        {
            Start = tableOffset,
            Length = totalSize,
            Text = $"{name} ({count} entries)",
            EntrySize = entrySize
        };
        PEFile.Add(table);
    }
}

public class DynamicValueRelocationTable : Node
{
    public DynamicValueRelocationTable()
    {
        Text = "Dynamic value relocation table";
    }

    public bool IsPE32Plus { get; set; }

    public override void Parse()
    {
        Version = AddFourBytes("Version");
        Size = AddFourBytes("Size");

        if (Version.Value != 1)
        {
            AddBytes(End - LastChildEnd, $"Dynamic relocations (version {Version.Value})");
            return;
        }

        int symbolSize = IsPE32Plus ? 8 : 4;
        while (LastChildEnd + symbolSize + 4 <= End)
        {
            long symbol = IsPE32Plus
                ? (long)Buffer.ReadUInt64(LastChildEnd)
                : Buffer.ReadUInt32(LastChildEnd);
            int baseRelocSize = Buffer.ReadInt32(LastChildEnd + symbolSize);
            if (baseRelocSize < 0 || LastChildEnd + symbolSize + 4 + baseRelocSize > End)
            {
                break;
            }

            var entry = new DynamicRelocationEntry
            {
                IsPE32Plus = IsPE32Plus,
                Length = symbolSize + 4 + baseRelocSize,
                Symbol = symbol
            };
            Add(entry);
        }

        int remaining = End - LastChildEnd;
        if (remaining > 0)
        {
            AddBytes(remaining, "Remaining bytes");
        }
    }

    public FourBytes Version { get; set; }
    public FourBytes Size { get; set; }
}

public class DynamicRelocationEntry : Node
{
    public bool IsPE32Plus { get; set; }
    public long Symbol { get; set; }

    public override void Parse()
    {
        var symbolNode = AddFourOrEightBytes(IsPE32Plus);
        symbolNode.Text = "Symbol";
        BaseRelocSize = AddFourBytes("Base reloc size");

        int dataLength = BaseRelocSize.Value;
        if (dataLength > 0)
        {
            AddBytes(dataLength, "Relocation blocks");
        }

        Text = $"Dynamic relocations: {GetSymbolName(Symbol)}";
    }

    public static string GetSymbolName(long symbol)
    {
        return symbol switch
        {
            1 => "Guard RF prologue",
            2 => "Guard RF epilogue",
            3 => "Guard import control transfer (Retpoline)",
            4 => "Guard indirect control transfer (Retpoline)",
            5 => "Guard switchtable branch (Retpoline)",
            6 => "ARM64X",
            7 => "Function override",
            8 => "ARM64 kernel import call transfer",
            _ => $"Symbol 0x{symbol:X}"
        };
    }

    public FourBytes BaseRelocSize { get; set; }
}

public class LoadConfigTable : Node
{
    public int EntrySize { get; set; }

    public override void Parse()
    {
        int count = Length / EntrySize;
        for (int i = 0; i < count; i++)
        {
            AddBytes(EntrySize, "Entry");
        }
    }
}
