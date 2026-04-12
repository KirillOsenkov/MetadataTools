namespace GuiLabs.FileFormat.PE;

public class BaseRelocationTable : Node
{
    public BaseRelocationTable()
    {
        Text = "Base reloc table";
    }

    public override void Parse()
    {
        int remaining = Length;

        while (remaining >= 8)
        {
            int blockStart = LastChildEnd;
            int pageRva = Buffer.ReadInt32(blockStart);
            int blockSize = Buffer.ReadInt32(blockStart + 4);

            if (blockSize < 8 || blockSize > remaining)
            {
                break;
            }

            var block = new BaseRelocationBlock
            {
                Start = blockStart,
                Length = blockSize
            };
            Add(block);

            remaining -= blockSize;
        }
    }
}

public class BaseRelocationBlock : Node
{
    public override void Parse()
    {
        var pageRva = Add<FourBytes>("Page RVA");
        var blockSize = Add<FourBytes>("Block Size");

        int entryCount = (Length - 8) / 2;
        for (int i = 0; i < entryCount; i++)
        {
            int offset = LastChildEnd;
            ushort value = Buffer.ReadUInt16(offset);
            int type = value >> 12;
            int relOffset = value & 0xFFF;

            string typeName = type switch
            {
                0 => "ABSOLUTE",
                1 => "HIGH",
                2 => "LOW",
                3 => "HIGHLOW",
                4 => "HIGHADJ",
                5 => "MIPS_JMPADDR",
                10 => "DIR64",
                _ => $"Type {type}"
            };

            string text = type == 0
                ? "ABSOLUTE (padding)"
                : $"{typeName} 0x{(pageRva.Value + relOffset):X}";

            var entry = Add<TwoBytes>(text);
        }
    }
}
