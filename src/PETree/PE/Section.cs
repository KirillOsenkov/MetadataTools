using System.Collections.Generic;

namespace GuiLabs.FileFormat.PE;

public class Section : Node
{
}

public class SectionTable : Node
{
    public SectionTable(short count)
    {
        Count = count;
        Text = "Section Table";
    }

    public short Count { get; }

    public override void Parse()
    {
        var list = new List<SectionHeader>();
        for (int i = 0; i < Count; i++)
        {
            list.Add(Add<SectionHeader>());
        }

        SectionHeaders = list;
    }

    public IReadOnlyList<SectionHeader> SectionHeaders { get; set; }
}

public class SectionHeader : Node
{
    public SectionHeader()
    {
        Text = "Section header";
    }

    public override void Parse()
    {
        Name = Add<EightByteString>("Name");
        VirtualSize = AddFourBytes("Virtual size");
        VirtualAddress = AddFourBytes("Virtual address");
        SizeOfRawData = AddFourBytes("Size of raw data");
        PointerToRawData = AddFourBytes("Pointer to raw data");
        PointerToRelocations = AddFourBytes("Pointer to relocations");
        PointerToLineNumbers = AddFourBytes("Pointer to line numbers");
        NumberOfRelocations = AddTwoBytes("Number of relocations");
        NumberOfLineNumbers = AddTwoBytes("Number of line numbers");
        Characteristics = AddFourBytes("Characteristics");

        Text = $"{Name.Text} section header";
    }

    public EightByteString Name { get; set; }
    public FourBytes VirtualSize { get; set; }
    public FourBytes VirtualAddress { get; set; }
    public FourBytes SizeOfRawData { get; set; }
    public FourBytes PointerToRawData { get; set; }
    public FourBytes PointerToRelocations { get; set; }
    public FourBytes PointerToLineNumbers { get; set; }
    public TwoBytes NumberOfRelocations { get; set; }
    public TwoBytes NumberOfLineNumbers { get; set; }
    public FourBytes Characteristics { get; set; }
}
