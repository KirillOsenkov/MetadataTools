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

        int remaining = actualSize - 4;
        if (remaining > 0)
        {
            AddBytes(remaining, "Load config fields");
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
