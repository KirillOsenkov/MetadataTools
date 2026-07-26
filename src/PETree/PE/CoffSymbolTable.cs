using System.Collections.Generic;
using System.Text;

namespace GuiLabs.FileFormat.PE;

public class CoffSymbolTable : Node
{
    public CoffSymbolTable()
    {
        Text = "COFF symbol table";
    }

    public int SymbolCount { get; set; }
    public CoffStringTable StringTable { get; set; }

    public override void Parse()
    {
        const int maxIndividualSymbols = 10000;

        var list = new List<CoffSymbol>();
        int remaining = SymbolCount;
        while (remaining > 0)
        {
            if (list.Count >= maxIndividualSymbols)
            {
                AddBytes(remaining * 18, $"Remaining {remaining} symbol records");
                break;
            }

            var symbol = Add<CoffSymbol>();
            list.Add(symbol);
            remaining--;

            int auxCount = symbol.NumberOfAuxSymbols.Value;
            if (auxCount > 0)
            {
                int auxToRead = System.Math.Min(auxCount, remaining);
                AddBytes(auxToRead * 18, $"Auxiliary records ({auxToRead})");
                remaining -= auxToRead;
            }
        }

        Symbols = list;
        Text = $"COFF symbol table ({SymbolCount} symbols)";
    }

    public void ApplyLongNames()
    {
        if (StringTable == null || Symbols == null)
        {
            return;
        }

        foreach (var symbol in Symbols)
        {
            symbol.ApplyLongName(StringTable);
        }
    }

    public IReadOnlyList<CoffSymbol> Symbols { get; set; }
}

public class CoffSymbol : Node
{
    public override void Parse()
    {
        Name = AddEightBytes("Name");
        Value = AddFourBytes("Value");
        SectionNumber = AddTwoBytes("Section number");
        SymbolType = AddTwoBytes("Type");
        StorageClass = AddOneByte("Storage class");
        NumberOfAuxSymbols = AddOneByte("Number of aux symbols");

        // A zero first dword means the name is in the string table (second
        // dword is the offset); otherwise it's an inline 8-byte name
        if (Buffer.ReadUInt32(Name.Start) != 0)
        {
            var bytes = Buffer.ReadBytes(Name.Start, 8);
            int length = 0;
            while (length < 8 && bytes[length] != 0)
            {
                length++;
            }

            Text = Encoding.UTF8.GetString(bytes, 0, length);
        }
        else
        {
            Text = "Symbol";
        }
    }

    public void ApplyLongName(CoffStringTable stringTable)
    {
        if (Buffer.ReadUInt32(Name.Start) != 0)
        {
            return;
        }

        int offset = Buffer.ReadInt32(Name.Start + 4);
        string name = stringTable.ReadString(offset);
        if (name != null)
        {
            Text = name;
        }
    }

    public EightBytes Name { get; set; }
    public FourBytes Value { get; set; }
    public TwoBytes SectionNumber { get; set; }
    public TwoBytes SymbolType { get; set; }
    public OneByte StorageClass { get; set; }
    public OneByte NumberOfAuxSymbols { get; set; }
}

public class CoffStringTable : Node
{
    public CoffStringTable()
    {
        Text = "COFF string table";
    }

    public override void Parse()
    {
        Size = AddFourBytes("Size");
        int remaining = Length - 4;
        if (remaining > 0)
        {
            AddBytes(remaining, "Strings");
        }
    }

    public string ReadString(int offset)
    {
        if (offset < 4 || offset >= Length)
        {
            return null;
        }

        var bytes = new List<byte>();
        for (int i = Start + offset; i < End; i++)
        {
            byte b = Buffer.ReadByte(i);
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public FourBytes Size { get; set; }
}
