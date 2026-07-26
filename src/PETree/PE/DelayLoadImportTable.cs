using System.Collections.Generic;

namespace GuiLabs.FileFormat.PE;

public class DelayLoadImportTable : Node
{
    public DelayLoadImportTable()
    {
        Text = "Delay-load import table";
    }

    public bool IsPE32Plus { get; set; }
    public PEFile PEFile { get; set; }

    public override void Parse()
    {
        var list = new List<DelayLoadDescriptor>();
        DelayLoadDescriptor descriptor;
        do
        {
            if (LastChildEnd + 32 > Buffer.Length)
            {
                break;
            }

            descriptor = Add<DelayLoadDescriptor>("Delay-load descriptor");
            list.Add(descriptor);
        } while (!Buffer.IsZeroFilled(descriptor.Span));

        Descriptors = list;

        if (PEFile == null)
        {
            return;
        }

        // Satellite tables (delay IAT, unload table) usually live in a
        // different section than the descriptors, so they are added to the
        // file root rather than stretching this node across sections
        foreach (var current in list)
        {
            ParseDescriptor(current);
        }
    }

    private void ParseDescriptor(DelayLoadDescriptor descriptor)
    {
        // Attributes bit 0 set means fields are RVAs (v1); clear means
        // virtual addresses that need the image base subtracted (v0)
        bool isRVA = (descriptor.Attributes.ReadUint32() & 1) != 0;

        int dllName = ResolveField(descriptor.DllNameRVA.Value, isRVA);
        if (dllName > 0 && PEFile.IsRangeAvailable(dllName, 1))
        {
            var dllNameNode = new ZeroTerminatedString { Start = dllName, Text = "Dll name" };
            PEFile.Add(dllNameNode);
            descriptor.Text = $"Delay-load: {dllNameNode.Text}";
        }

        int nameTable = ResolveField(descriptor.ImportNameTableRVA.Value, isRVA);
        int nameTableSize = MeasurePointerTable(nameTable);
        if (nameTableSize > 0 && PEFile.IsRangeAvailable(nameTable, nameTableSize))
        {
            var importNameTable = new ImportLookupTable
            {
                Start = nameTable,
                IsPE32Plus = IsPE32Plus,
                Text = "Delay import name table"
            };
            PEFile.Add(importNameTable);

            for (int i = 0; i < importNameTable.Entries.Count - 1; i++)
            {
                AddImportName(importNameTable.Entries[i].Start, isRVA);
            }
        }

        AddPointerTable(descriptor.ImportAddressTableRVA.Value, isRVA, "Delay import address table");
        AddPointerTable(descriptor.BoundImportAddressTableRVA.Value, isRVA, "Delay bound import address table");
        AddPointerTable(descriptor.UnloadInformationTableRVA.Value, isRVA, "Delay unload information table");
    }

    private void AddImportName(int entryOffset, bool isRVA)
    {
        long value = IsPE32Plus
            ? (long)Buffer.ReadUInt64(entryOffset)
            : (long)Buffer.ReadUInt32(entryOffset);
        if (value == 0)
        {
            return;
        }

        bool isOrdinal = IsPE32Plus ? (value & unchecked((long)0x8000000000000000)) != 0 : (value & 0x80000000) != 0;
        if (isOrdinal)
        {
            return;
        }

        int resolved = ResolveField((int)(value & 0x7FFFFFFF), isRVA);
        if (resolved > 0 && PEFile.IsRangeAvailable(resolved, 3))
        {
            var imageImportByName = new ImageImportByName { Start = resolved };
            PEFile.Add(imageImportByName);
        }
    }

    private int MeasurePointerTable(int offset)
    {
        if (offset <= 0)
        {
            return 0;
        }

        int entrySize = IsPE32Plus ? 8 : 4;
        int count = 0;
        bool foundTerminator = false;
        for (int current = offset; current + entrySize <= Buffer.Length; current += entrySize)
        {
            count++;
            ulong value = IsPE32Plus
                ? Buffer.ReadUInt64(current)
                : Buffer.ReadUInt32(current);
            if (value == 0)
            {
                foundTerminator = true;
                break;
            }
        }

        return foundTerminator ? count * entrySize : 0;
    }

    private void AddPointerTable(int fieldValue, bool isRVA, string text)
    {
        int offset = ResolveField(fieldValue, isRVA);
        int tableSize = MeasurePointerTable(offset);
        if (tableSize == 0 || !PEFile.IsRangeAvailable(offset, tableSize))
        {
            return;
        }

        var table = new Node
        {
            Start = offset,
            Length = tableSize,
            Text = text
        };
        PEFile.Add(table);
    }

    private int ResolveField(int fieldValue, bool isRVA)
    {
        if (fieldValue == 0)
        {
            return 0;
        }

        int rva = fieldValue;
        if (!isRVA)
        {
            ulong imageBase = IsPE32Plus
                ? Buffer.ReadUInt64(PEFile.OptionalHeader.WindowsFields.ImageBase.Start)
                : Buffer.ReadUInt32(PEFile.OptionalHeader.WindowsFields.ImageBase.Start);
            ulong virtualAddress = (uint)fieldValue;
            if (virtualAddress < imageBase || virtualAddress - imageBase > int.MaxValue)
            {
                return 0;
            }

            rva = (int)(virtualAddress - imageBase);
        }

        return PEFile.ResolveVirtualAddress(rva);
    }

    public IReadOnlyList<DelayLoadDescriptor> Descriptors { get; set; }
}

public class DelayLoadDescriptor : Node
{
    public override void Parse()
    {
        Attributes = AddFourBytes("Attributes");
        DllNameRVA = AddFourBytes("Dll name RVA");
        ModuleHandleRVA = AddFourBytes("Module handle RVA");
        ImportAddressTableRVA = AddFourBytes("Import address table RVA");
        ImportNameTableRVA = AddFourBytes("Import name table RVA");
        BoundImportAddressTableRVA = AddFourBytes("Bound import address table RVA");
        UnloadInformationTableRVA = AddFourBytes("Unload information table RVA");
        TimeDateStamp = AddFourBytes("TimeDate stamp");
    }

    public FourBytes Attributes { get; set; }
    public FourBytes DllNameRVA { get; set; }
    public FourBytes ModuleHandleRVA { get; set; }
    public FourBytes ImportAddressTableRVA { get; set; }
    public FourBytes ImportNameTableRVA { get; set; }
    public FourBytes BoundImportAddressTableRVA { get; set; }
    public FourBytes UnloadInformationTableRVA { get; set; }
    public FourBytes TimeDateStamp { get; set; }
}
