namespace GuiLabs.FileFormat.PE;

public class ExportTable : Node
{
    public ExportTable()
    {
        Text = "Export table";
    }

    public PEFile PEFile { get; set; }

    public override void Parse()
    {
        if (PEFile == null)
        {
            return;
        }

        // Export Directory Table (40 bytes)
        var exportFlags = Add<FourBytes>("Export flags");
        var timeDateStamp = Add<FourBytes>("Time/date stamp");
        var majorVersion = Add<TwoBytes>("Major version");
        var minorVersion = Add<TwoBytes>("Minor version");
        var nameRva = Add<FourBytes>("Name RVA");
        var ordinalBase = Add<FourBytes>("Ordinal base");
        var addressTableEntries = Add<FourBytes>("Address table entries");
        var numberOfNamePointers = Add<FourBytes>("Number of name pointers");
        var exportAddressTableRva = Add<FourBytes>("Export address table RVA");
        var namePointerRva = Add<FourBytes>("Name pointer RVA");
        var ordinalTableRva = Add<FourBytes>("Ordinal table RVA");

        int numAddresses = addressTableEntries.Value;
        int numNames = numberOfNamePointers.Value;

        // Export Address Table
        if (exportAddressTableRva.Value != 0 && numAddresses > 0)
        {
            int eatOffset = PEFile.ResolveVirtualAddress(exportAddressTableRva.Value);
            if (eatOffset > 0)
            {
                var eat = new Node
                {
                    Start = eatOffset,
                    Length = numAddresses * 4,
                    Text = "Export address table"
                };
                Add(eat);

                // Check for forwarder strings (RVA within export directory range)
                int exportRvaStart = exportAddressTableRva.Value - (eatOffset - Start);
                int exportRvaEnd = exportRvaStart + Length;
                for (int i = 0; i < numAddresses; i++)
                {
                    int entryRva = PEFile.Buffer.ReadInt32(eatOffset + i * 4);
                    if (entryRva >= exportRvaStart && entryRva < exportRvaEnd)
                    {
                        int forwarderOffset = PEFile.ResolveVirtualAddress(entryRva);
                        if (forwarderOffset > 0)
                        {
                            Add(new ZeroTerminatedString { Start = forwarderOffset });
                        }
                    }
                }
            }
        }

        // Name Pointer Table
        int nptOffset = 0;
        if (namePointerRva.Value != 0 && numNames > 0)
        {
            nptOffset = PEFile.ResolveVirtualAddress(namePointerRva.Value);
            if (nptOffset > 0)
            {
                var npt = new Node
                {
                    Start = nptOffset,
                    Length = numNames * 4,
                    Text = "Name pointer table"
                };
                Add(npt);
            }
        }

        // Ordinal Table
        if (ordinalTableRva.Value != 0 && numNames > 0)
        {
            int otOffset = PEFile.ResolveVirtualAddress(ordinalTableRva.Value);
            if (otOffset > 0)
            {
                var ot = new Node
                {
                    Start = otOffset,
                    Length = numNames * 2,
                    Text = "Ordinal table"
                };
                Add(ot);
            }
        }

        // DLL name string
        if (nameRva.Value != 0)
        {
            int nameOffset = PEFile.ResolveVirtualAddress(nameRva.Value);
            if (nameOffset > 0)
            {
                Add(new ZeroTerminatedString { Start = nameOffset });
            }
        }

        // Export name strings from the Name Pointer Table
        if (nptOffset > 0 && numNames > 0)
        {
            for (int i = 0; i < numNames; i++)
            {
                int entryRva = PEFile.Buffer.ReadInt32(nptOffset + i * 4);
                if (entryRva != 0)
                {
                    int nameStringOffset = PEFile.ResolveVirtualAddress(entryRva);
                    if (nameStringOffset > 0)
                    {
                        Add(new ZeroTerminatedString { Start = nameStringOffset });
                    }
                }
            }
        }
    }
}
