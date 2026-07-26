using System.Collections.Generic;

namespace GuiLabs.FileFormat.PE;

public class RichHeader : Node
{
    public RichHeader()
    {
        Text = "Rich Header";
    }

    public override void Parse()
    {
        // Layout: DanS ^ key, three zero dwords ^ key, then (compId ^ key, count ^ key)
        // pairs, terminated by the plaintext "Rich" signature and the 4-byte XOR key
        uint xorKey = Buffer.ReadUInt32(End - 4);

        DanS = AddFourBytes("DanS signature (encrypted)");
        AddFourBytes("Padding (encrypted)");
        AddFourBytes("Padding (encrypted)");
        AddFourBytes("Padding (encrypted)");

        var list = new List<RichHeaderEntry>();
        while (LastChildEnd <= End - 16)
        {
            var entry = new RichHeaderEntry { XorKey = xorKey };
            Add(entry);
            list.Add(entry);
        }

        Entries = list;

        RichSignature = AddFourBytes("Rich signature");
        XorKeyNode = AddFourBytes("XOR key");
    }

    public FourBytes DanS { get; set; }
    public FourBytes RichSignature { get; set; }
    public FourBytes XorKeyNode { get; set; }
    public IReadOnlyList<RichHeaderEntry> Entries { get; set; }
}

public class RichHeaderEntry : Node
{
    public uint XorKey { get; set; }

    public override void Parse()
    {
        CompId = AddFourBytes("Comp id");
        Count = AddFourBytes("Count");

        uint compId = CompId.ReadUint32() ^ XorKey;
        uint count = Count.ReadUint32() ^ XorKey;

        ProductId = (int)(compId >> 16);
        Build = (int)(compId & 0xFFFF);
        UseCount = (int)count;

        string tool = GetToolName(ProductId);
        Text = $"{tool} (id 0x{ProductId:X2}) build {Build}: used {UseCount} {(UseCount == 1 ? "time" : "times")}";
    }

    public FourBytes CompId { get; set; }
    public FourBytes Count { get; set; }
    public int ProductId { get; set; }
    public int Build { get; set; }
    public int UseCount { get; set; }

    public static string GetToolName(int productId)
    {
        switch (productId)
        {
            case 0x00: return "Unknown";
            case 0x01: return "Import (old)";
            case 0x5E: return "Imports (VS2002)";
            case 0x7A: return "Imports (VS2003)";
            case 0x93: return "Imports (VS2005)";
            case 0x9A: return "Resource (VS2008)";
            case 0xAA: return "MASM (VS2010)";
            case 0xC9: return "Resource (VS2012)";
            case 0xCB: return "MASM (VS2012)";
            case 0xD9: return "Resource (VS2013)";
            case 0xDB: return "MASM (VS2013)";
            case 0xFF: return "Resource (VS2015+)";
            case 0x100: return "Linker (VS2015+)";
            case 0x101: return "MASM (VS2015+)";
            case 0x102: return "Exports (VS2015+)";
            case 0x103: return "Imports (VS2015+)";
            case 0x104: return "C++ compiler (VS2015+)";
            case 0x105: return "C compiler (VS2015+)";
            case 0x109: return "C++ compiler LTCG (VS2015+)";
            case 0x10A: return "C compiler LTCG (VS2015+)";
            case 0x10D: return "C++ compiler PGO (VS2015+)";
            case 0x10E: return "C compiler PGO (VS2015+)";
        }

        if (productId >= 0xE0 && productId <= 0x10E)
        {
            return "VS2015+ tool";
        }

        return "Tool";
    }
}
