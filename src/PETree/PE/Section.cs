using System.Collections.Generic;
using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class Section : Node
{
    public string Name { get; set; }
}

public class SectionTable : Node
{
    public SectionTable(short count)
    {
        Count = count;
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
    public override void Parse()
    {
        Name = Add<EightByteString>();
        VirtualSize = AddFourBytes();
        VirtualAddress = AddFourBytes();
        SizeOfRawData = AddFourBytes();
        PointerToRawData = AddFourBytes();
        PointerToRelocations = AddFourBytes();
        PointerToLineNumbers = AddFourBytes();
        NumberOfRelocations = AddTwoBytes();
        NumberOfLineNumbers = AddTwoBytes();
        Characteristics = AddFourBytes();
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
