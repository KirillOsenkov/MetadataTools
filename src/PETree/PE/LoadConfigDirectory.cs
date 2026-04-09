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
        if (actualSize <= 0 || actualSize > Length)
        {
            return;
        }

        AddFourBytes("Size");

        // We use the declared size to know how far to read
        // For PE32+, the GuardCF fields start at offset 0x80 from LoadConfig start
        // Minimum size to have Guard CF: 0x94 (offset 0x90 + 4 for GuardFlags)
        if (!IsPE32Plus || actualSize < 0x94)
        {
            // Add remaining as raw bytes
            int remaining = actualSize - (LastChildEnd - Start);
            if (remaining > 0)
            {
                AddBytes(remaining, "Load config data");
            }

            Length = LastChildEnd - Start;
            return;
        }

        // Read up to GuardFlags to get entry size info
        int preGuardSize = 0x90 - 4; // bytes between Size field and GuardFlags field
        AddBytes(preGuardSize, "Load config fields");

        var guardFlagsNode = AddFourBytes("GuardFlags");
        int guardFlags = guardFlagsNode.Value;
        int extraBytes = (guardFlags >> 28) & 0xF;
        int guardEntrySize = 4 + extraBytes;

        // Read remaining LoadConfig data
        int remainingLcData = actualSize - (LastChildEnd - Start);
        if (remainingLcData > 0)
        {
            AddBytes(remainingLcData, "Load config extended fields");
        }

        Length = LastChildEnd - Start;

        // Now add Guard CF tables as children of PEFile (not of LoadConfig, since they're at different file offsets)
        if (PEFile == null)
        {
            return;
        }

        long imageBase = IsPE32Plus
            ? (long)Buffer.ReadUInt64(PEFile.OptionalHeader.WindowsFields.ImageBase.Start)
            : Buffer.ReadInt32(PEFile.OptionalHeader.WindowsFields.ImageBase.Start);

        // GuardCFFunctionTable at offset 0x80, count at 0x88
        TryAddGuardTable(0x80, 0x88, actualSize, imageBase, guardEntrySize, "Guard CF function table");

        // GuardCFAddressTakenIatEntryTable at offset 0xA0, count at 0xA8
        TryAddGuardTable(0xA0, 0xA8, actualSize, imageBase, guardEntrySize, "Guard CF IAT table");

        // GuardLongJumpTargetTable at offset 0xB0, count at 0xB8
        TryAddGuardTable(0xB0, 0xB8, actualSize, imageBase, guardEntrySize, "Guard CF long jump table");

        // GuardEHContinuationTable at offset 0xC8, count at 0xD0
        if (actualSize >= 0xD8)
        {
            TryAddGuardTable(0xC8, 0xD0, actualSize, imageBase, guardEntrySize, "Guard EH continuation table");
        }
    }

    private void TryAddGuardTable(int vaFieldOffset, int countFieldOffset, int actualSize, long imageBase, int entrySize, string name)
    {
        if (actualSize < countFieldOffset + 8)
        {
            return;
        }

        long tableVA = (long)Buffer.ReadUInt64(Start + vaFieldOffset);
        long count = (long)Buffer.ReadUInt64(Start + countFieldOffset);

        if (tableVA == 0 || count <= 0)
        {
            return;
        }

        int tableRva = (int)(tableVA - imageBase);
        int tableOffset = PEFile.ResolveVirtualAddress(tableRva);
        if (tableOffset <= 0)
        {
            return;
        }

        int totalSize = (int)(count * entrySize);
        var table = new Node
        {
            Start = tableOffset,
            Length = totalSize,
            Text = $"{name} ({count} entries)"
        };
        PEFile.Add(table);
    }
}
