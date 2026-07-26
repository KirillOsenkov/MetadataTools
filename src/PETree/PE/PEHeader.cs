using System.Collections.Generic;

namespace GuiLabs.FileFormat.PE;

public class PEHeader : Node
{
    public PEHeader()
    {
        Text = "PE Header";
    }

    public override void Parse()
    {
        PEHeaderSignature = AddFourBytes("PE Header signature");

        Platform = AddTwoBytes("Platform");
        Platform.Text = $"Platform: {GetMachineTypeName(Platform.Value)}";
        NumberOfSections = AddTwoBytes("Number of sections");
        TimeDateStamp = AddFourBytes("Time-date stamp");
        PointerToSymbolTable = AddFourBytes("Pointer to symbol table");
        NumberOfSymbols = AddFourBytes("Number of symbols");
        SizeOfOptionalHeader = AddTwoBytes("Size of optional header");
        Characteristics = AddTwoBytes("Characteristics");
    }

    public static string GetMachineTypeName(int machine)
    {
        return (ushort)machine switch
        {
            0x0000 => "Unknown",
            0x014C => "x86",
            0x0166 => "MIPS R4000",
            0x01A2 => "SH3",
            0x01A6 => "SH4",
            0x01C0 => "ARM",
            0x01C2 => "Thumb",
            0x01C4 => "ARM Thumb-2 (ARMNT)",
            0x0200 => "Itanium (IA64)",
            0x0EBC => "EFI bytecode",
            0x5032 => "RISC-V 32",
            0x5064 => "RISC-V 64",
            0x6232 => "LoongArch 32",
            0x6264 => "LoongArch 64",
            0x8664 => "x64 (AMD64)",
            0xA641 => "ARM64EC",
            0xA64E => "ARM64X",
            0xAA64 => "ARM64",
            _ => $"0x{(ushort)machine:X4}"
        };
    }

    public FourBytes PEHeaderSignature { get; set; }
    public TwoBytes Platform { get; set; }
    public TwoBytes NumberOfSections { get; set; }
    public FourBytes TimeDateStamp { get; set; }
    public FourBytes PointerToSymbolTable { get; set; }
    public FourBytes NumberOfSymbols { get; set; }
    public TwoBytes SizeOfOptionalHeader { get; set; }
    public TwoBytes Characteristics { get; set; }
}

public class DataDirectory : EightBytes
{
    public DataDirectory() : base()
    {
        Text = "Data Directory";
    }

    public override void Parse()
    {
        RVA = AddFourBytes("RVA");
        Size = AddFourBytes("Size");
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
}

public class BoundImport : Node
{
    public BoundImport()
    {
        Text = "Bound import";
    }

    public override void Parse()
    {
        int tableStart = Start;
        var descriptors = new List<BoundImportDescriptor>();

        while (LastChildEnd + 8 <= End)
        {
            uint timeDateStamp = Buffer.ReadUInt32(LastChildEnd);
            ushort nameOffset = Buffer.ReadUInt16(LastChildEnd + 4);
            ushort forwarderCount = Buffer.ReadUInt16(LastChildEnd + 6);
            if (timeDateStamp == 0 && nameOffset == 0 && forwarderCount == 0)
            {
                break;
            }

            var descriptor = Add<BoundImportDescriptor>();
            descriptors.Add(descriptor);

            for (int i = 0; i < forwarderCount && LastChildEnd + 8 <= End; i++)
            {
                var forwarder = Add<BoundImportDescriptor>("Bound forwarder ref");
                descriptors.Add(forwarder);
            }
        }

        // Name strings follow the descriptors; the same name can be
        // referenced by several descriptors, so add each string once
        int descriptorsEnd = LastChildEnd;
        var namesByOffset = new Dictionary<int, string>();
        foreach (var descriptor in descriptors)
        {
            int nameOffset = (ushort)descriptor.OffsetModuleName.Value;
            if (nameOffset == 0 ||
                tableStart + nameOffset < descriptorsEnd ||
                tableStart + nameOffset >= Buffer.Length)
            {
                continue;
            }

            if (!namesByOffset.TryGetValue(nameOffset, out var nameText))
            {
                var name = new ZeroTerminatedString { Start = tableStart + nameOffset };
                Add(name);
                nameText = name.Text;
                namesByOffset[nameOffset] = nameText;
            }

            descriptor.Text = $"{descriptor.Text}: {nameText}";
        }

        AddRemainingPadding();
    }
}

public class BoundImportDescriptor : Node
{
    public BoundImportDescriptor()
    {
        Text = "Bound import descriptor";
    }

    public override void Parse()
    {
        TimeDateStamp = AddFourBytes("TimeDate stamp");
        OffsetModuleName = AddTwoBytes("Offset of module name");
        NumberOfModuleForwarderRefs = AddTwoBytes("Number of module forwarder refs");
    }

    public FourBytes TimeDateStamp { get; set; }
    public TwoBytes OffsetModuleName { get; set; }
    public TwoBytes NumberOfModuleForwarderRefs { get; set; }
}

public class ImportTable : Node
{
    public ImportTable()
    {
        Text = "Import table";
    }

    public override void Parse()
    {
        var list = new List<ImportsDirectory>();
        while (LastChildEnd + 20 <= Buffer.Length)
        {
            var importsDirectory = Add<ImportsDirectory>("Imports directory");
            list.Add(importsDirectory);
            if (Buffer.IsZeroFilled(importsDirectory.Span))
            {
                break;
            }
        }

        ImportsDirectories = list.ToArray();
    }

    public IReadOnlyList<ImportsDirectory> ImportsDirectories { get; set; }
}

public class ImportsDirectory : Node
{
    public override void Parse()
    {
        LookupTableRVA = AddFourBytes("Lookup table RVA");
        TimeDateStamp = AddFourBytes("TimeDate stamp");
        ForwarderChain = AddFourBytes("Forwarder chain");
        DllNameRVA = AddFourBytes("Dll name RVA");
        AddressTableRVA = AddFourBytes("Address table RVA");
    }

    public FourBytes LookupTableRVA { get; set; }
    public FourBytes TimeDateStamp { get; set; }
    public FourBytes ForwarderChain { get; set; }
    public FourBytes DllNameRVA { get; set; }
    public FourBytes AddressTableRVA { get; set; }
}

public class IAT : Node
{
    public IAT()
    {
        Text = "Import Address Table";
    }

    public bool IsPE32Plus { get; set; }

    public override void Parse()
    {
        var list = new List<BytesNode>();
        int entrySize = IsPE32Plus ? 8 : 4;
        int entryCount = Length / entrySize;
        for (int i = 0; i < entryCount; i++)
        {
            BytesNode entry = IsPE32Plus ? AddEightBytes("Entry") : AddFourBytes("Entry");
            list.Add(entry);
        }

        int trailingBytes = Length % entrySize;
        if (trailingBytes > 0)
        {
            AddBytes(trailingBytes, "Trailing bytes");
        }

        Entries = list.ToArray();
    }

    public IReadOnlyList<BytesNode> Entries { get; set; }
}

public class ImageImportByName : Node
{
    public ImageImportByName()
    {
        Text = "IMAGE_IMPORT_BY_NAME";
    }

    public override void Parse()
    {
        Hint = AddTwoBytes("Hint");
        Name = Add<ZeroTerminatedString>("Name");
    }

    public TwoBytes Hint { get; set; }
    public ZeroTerminatedString Name { get; set; }
}

public class ImportLookupTable : Node
{
    public ImportLookupTable()
    {
        Text = "Import Lookup Table";
    }

    public bool IsPE32Plus { get; set; }

    public override void Parse()
    {
        var list = new List<BytesNode>();
        int entrySize = IsPE32Plus ? 8 : 4;
        while (LastChildEnd + entrySize <= Buffer.Length)
        {
            BytesNode entry = IsPE32Plus ? AddEightBytes("Entry") : AddFourBytes("Entry");
            list.Add(entry);
            if (Buffer.IsZeroFilled(entry.Span))
            {
                break;
            }
        }

        Entries = list.ToArray();
    }

    public IReadOnlyList<BytesNode> Entries { get; set; }
}