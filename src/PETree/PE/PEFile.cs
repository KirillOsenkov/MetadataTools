using System.IO;
using System.Linq;
using System.Text;
using GuiLabs.FileFormat.PE.Dotnet;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat.PE;

public class PEFile : Node
{
    public string FilePath { get; set; }

    public PEFile(ByteBuffer buffer)
    {
        Buffer = buffer;
    }

    public static PEFile ReadFromFile(string filePath, bool inMemory = true)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open);
        Stream stream = fileStream;
        if (inMemory)
        {
            var memoryStream = new MemoryStream((int)fileStream.Length);
            fileStream.CopyTo(memoryStream);
            memoryStream.Position = 0;
            stream = memoryStream;
        }

        var buffer = new StreamBuffer(stream);
        var peFile = new PEFile(buffer);
        peFile.Parse();
        return peFile;
    }

    public void WriteToFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Create);
        Write(stream);
    }

    public string GetText()
    {
        var sb = new StringBuilder();
        var writer = new StringWriter(sb);

        Write(writer, "");

        return sb.ToString();
    }

    public bool IsPE32Plus => OptionalHeader.StandardFields.IsPE32Plus;

    public override void Parse()
    {
        DOSHeader = Add<DOSHeader>("DOS Header");

        var DOSStub = new Node { Length = 0x40, Text = "DOS Stub" };
        Add(DOSStub);

        int peHeaderPointer = DOSHeader.CoffHeaderPointer.Value;
        if (peHeaderPointer == 0)
        {
            peHeaderPointer = 0x80;
        }

        PEHeader = new PEHeader { Start = peHeaderPointer, Text = "PE Header" };
        Add(PEHeader);

        OptionalHeader = new OptionalHeader(PEHeader.SizeOfOptionalHeader.Value);
        Add(OptionalHeader);

        SectionTable = new SectionTable(PEHeader.NumberOfSections.Value);
        Add(SectionTable);

        TextSection = AddSection(".text");
        RsrcSection = AddSection(".rsrc");
        RelocSection = AddSection(".reloc");

        var sectionGap = new Span(SectionTable.End, TextSection.Start - SectionTable.End);
        if (sectionGap.Length > 0)
        {
            if (Buffer.IsZeroFilled(sectionGap))
            {
                SectionTable.AddPadding(sectionGap.Length);
            }
        }

        int cliHeader = ResolveDataDirectory(OptionalHeader.DataDirectories.CLRRuntimeHeader);
        if (cliHeader > 0)
        {
            ReadDotnetMetadata(cliHeader);
        }

        var debugDirectoryAddress = OptionalHeader.DataDirectories.Debug;
        if (debugDirectoryAddress.RVA.Value != 0)
        {
            int offset = ResolveDataDirectory(debugDirectoryAddress);
            DebugDirectories = new DebugDirectories { Start = offset, Length = debugDirectoryAddress.Size.Value };
            TextSection.Add(DebugDirectories);

            foreach (var debugDirectory in DebugDirectories.Directories)
            {
                var address = debugDirectory.AddressOfRawData.Value;
                var start = ResolveVirtualAddress(address);
                if (start > 0)
                {
                    Node entry = null;
                    if (debugDirectory.DirectoryType == DebugDirectory.ImageDebugType.EmbeddedPortablePdb)
                    {
                        EmbeddedPdb = new EmbeddedPdb
                        {
                            Start = start,
                            Length = debugDirectory.SizeOfData.Value
                        };
                        entry = EmbeddedPdb;
                    }
                    else if (debugDirectory.DirectoryType == DebugDirectory.ImageDebugType.CodeView && Buffer.ReadUInt32(start) == 0x53445352)
                    {
                        entry = RSDS = new RSDS() { Start = start };
                    }
                    else
                    {
                        entry = new DebugDirectoryEntry
                        {
                            Start = start,
                            Length = debugDirectory.SizeOfData.Value,
                            Text = $"{debugDirectory.DirectoryType}"
                        };
                    }

                    TextSection.Add(entry);
                }
            }
        }

        ResourceTable = AddTable<ResourceTable>(OptionalHeader.DataDirectories.ResourceTable, text: "Resource table");

        ImportAddressTable = AddTable<IAT>(OptionalHeader.DataDirectories.IAT);
        ImportTable = AddTable<ImportTable>(OptionalHeader.DataDirectories.ImportTable, text: "Import table");

        ParseImportTable();

        if (OptionalHeader.StandardFields.AddressOfEntryPoint is { } entrypointBytes &&
            entrypointBytes.Value is int entrypointRVA &&
            entrypointRVA != 0)
        {
            int entryPointOffset = ResolveVirtualAddress(entrypointRVA);
            RuntimeStartupStub runtimeStartupStub = null;
            if (IsPE32Plus)
            {
                entryPointOffset -= 6;
                runtimeStartupStub = new RuntimeStartupStub { Start = entryPointOffset, Length = 16 };
            }
            else
            {
                entryPointOffset -= 2;
                runtimeStartupStub = new RuntimeStartupStub { Start = entryPointOffset, Length = 8 };
            }

            Add(runtimeStartupStub);
        }

        AddTable<Node>(OptionalHeader.DataDirectories.BaseRelocationTable, text: "Base reloc table");
        AddTable<BoundImport>(OptionalHeader.DataDirectories.BoundImport, text: "Bound import");
        AddCertificateTable(OptionalHeader.DataDirectories.CertificateTable);
        AddTable<Node>(OptionalHeader.DataDirectories.ExceptionTable, text: "Exception table");
        AddTable<Node>(OptionalHeader.DataDirectories.ExportTable, text: "Export table");
        AddTable<Node>(OptionalHeader.DataDirectories.LoadConfigTable, text: "Load config table");
        AddTable<Node>(OptionalHeader.DataDirectories.TLSTable, text: "Thread Local Storage table");

        TextSection.AddRemainingPadding();
        RsrcSection?.AddRemainingPadding();
        RelocSection?.AddRemainingPadding();

        this.ValidateOverlap(node =>
        {
            throw new System.Exception($"Node {node} overlaps with its successor");
        });

        this.ComputeUncoveredSpans(span =>
        {
            if (Buffer.IsZeroFilled(span))
            {
                var padding = new Padding
                {
                    Start = span.Start,
                    Length = span.Length
                };
                Add(padding);
            }
            else
            {
                var unknown = new Unknown
                {
                    Start = span.Start,
                    Length = span.Length
                };
                this.Add(unknown);
            }
        });

        Text = $"PE File";
    }

    private void ReadDotnetMetadata(int cliHeader)
    {
        CLIHeader = new CLIHeader { Start = cliHeader };
        TextSection.Add(CLIHeader);

        var metadataDirectory = CLIHeader.Metadata;
        MetadataRVA = metadataDirectory.RVA.Value;
        MetadataSectionHeader = GetSectionAtVirtualAddress(MetadataRVA);
        int metadata = ResolveDataDirectory(metadataDirectory);

        if (metadata > 0)
        {
            var il = new IL
            {
                Start = CLIHeader.End,
                Length = metadata - CLIHeader.End
            };
            TextSection.Add(il);

            Metadata = new Metadata { Start = metadata };
            TextSection.Add(Metadata);

            il.FillWithPadding();
        }

        AddTable<StrongNameSignature>(CLIHeader.StrongNameSignature);
    }

    private Node AddCertificateTable(DataDirectory dataDirectory)
    {
        if (dataDirectory.Size.Value > 0)
        {
            var offset = dataDirectory.RVA.Value;
            var resolved = offset;
            if (resolved == 0)
            {
                resolved = offset;
            }

            var node = new CertificateTable
            {
                Start = resolved,
                Length = dataDirectory.Size.Value
            };

            Add(node);
            return node;
        }

        return null;
    }

    private T AddTable<T>(DataDirectory dataDirectory, bool isRVA = true, string text = null) where T : Node, new()
    {
        if (dataDirectory.Size.Value > 0)
        {
            var offset = dataDirectory.RVA.Value;
            var resolved = isRVA ? ResolveVirtualAddress(offset) : offset;
            if (resolved == 0)
            {
                resolved = offset;
            }

            var node = new T
            {
                Start = resolved,
                Length = dataDirectory.Size.Value
            };
            if (text != null)
            {
                node.Text = text;
            }

            Add(node);
            return node;
        }

        return null;
    }

    private Section AddSection(string name)
    {
        var sectionHeader = SectionTable.SectionHeaders.FirstOrDefault(s => s.Name.Text == name);
        if (sectionHeader != null)
        {
            var section = new Section
            {
                Start = sectionHeader.PointerToRawData.Value,
                Length = sectionHeader.SizeOfRawData.Value,
                Text = name
            };
            Add(section);
            return section;
        }

        return null;
    }

    private void ParseImportTable()
    {
        if (ImportTable == null)
        {
            return;
        }

        var list = ImportTable.ImportsDirectories;

        for (int i = 0; i < list.Count - 1; i++)
        {
            var directory = list[i];
            var addressTableRva = directory.AddressTableRVA;
            var dllNameRva = directory.DllNameRVA;
            var lookupTableRva = directory.LookupTableRVA;

            var lookupTable = ResolveVirtualAddress(lookupTableRva.Value);

            var importLookupTable = new ImportLookupTable() { Start = lookupTable };
            ImportTable.Add(importLookupTable);

            for (int j = 0; j < importLookupTable.Entries.Count - 1; j++)
            {
                var fourBytes = importLookupTable.Entries[j];

                var rva = fourBytes.Value;
                if (rva == 0)
                {
                    continue;
                }

                if (rva > 0)
                {
                    var resolved = ResolveVirtualAddress(rva);
                    var imageImportByName = new ImageImportByName() { Start = resolved };
                    ImportTable.Add(imageImportByName);
                }
            }

            var addressTable = ResolveVirtualAddress(addressTableRva.Value);
            var dllName = ResolveVirtualAddress(dllNameRva.Value);
            if (dllName > 0)
            {
                var dllNameNode = new ZeroTerminatedString() { Start = dllName };
                ImportTable.Add(dllNameNode);
            }
        }
    }

    public DOSHeader DOSHeader { get; set; }
    public PEHeader PEHeader { get; set; }
    public OptionalHeader OptionalHeader { get; set; }
    public SectionTable SectionTable { get; set; }
    public CLIHeader CLIHeader { get; set; }
    public Metadata Metadata { get; set; }
    public SectionHeader MetadataSectionHeader { get; set; }
    public int MetadataRVA { get; set; }
    public DebugDirectories DebugDirectories { get; set; }
    public EmbeddedPdb EmbeddedPdb { get; set; }
    public RSDS RSDS { get; set; }
    public Node ResourceTable { get; set; }
    public Node TextSection { get; set; }
    public Node RsrcSection { get; set; }
    public Node RelocSection { get; set; }
    public ImportTable ImportTable { get; set; }
    public IAT ImportAddressTable { get; set; }

    public int ResolveDataDirectory(DataDirectory dataDirectory)
    {
        return ResolveVirtualAddress(dataDirectory.RVA.Value);
    }

    public int ResolveVirtualAddress(int rva)
    {
        var section = GetSectionAtVirtualAddress(rva);
        if (section == null)
        {
            return 0;
        }

        return ResolveVirtualAddressInSection(rva, section);
    }

    public int ResolveMetadataOffset(int offset)
    {
        var result = MetadataRVA - MetadataSectionHeader.VirtualAddress.Value + offset;
        result = result + MetadataSectionHeader.PointerToRawData.Value;
        return result;
    }

    public int ResolveVirtualAddressInSection(int rva, SectionHeader section)
    {
        return (int)(rva + section.PointerToRawData.Value - section.VirtualAddress.Value);
    }

    public SectionHeader GetSectionAtVirtualAddress(int rva)
    {
        var sections = SectionTable.SectionHeaders;
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var virtualAddress = section.VirtualAddress.Value;
            if (rva >= virtualAddress && rva < virtualAddress + section.SizeOfRawData.Value)
            {
                return section;
            }
        }

        return null;
    }
}

public class DOSHeader : Node
{
    public override void Parse()
    {
        MZSignature = AddTwoBytes("MZ Signature");
        ExtraPageSize = AddTwoBytes("Extra page size");
        NumberOfPages = AddTwoBytes("Number of pages");
        Relocations = AddTwoBytes("Relocations");
        HeaderSizeInParagraphs = AddTwoBytes("Header size in paragraphs");
        MinimumAllocatedParagraphs = AddTwoBytes("Minimum allocated paragraphs");
        MaximumAllocatedParagraphs = AddTwoBytes("Maximum allocated paragraphs");
        InitialSSValue = AddTwoBytes("Initial SS value");
        InitialRelativeSPValue = AddTwoBytes("Initial relative SP value");
        Checksum = AddTwoBytes("Checksum");
        InitialRelativeIPValue = AddTwoBytes("Initial relative IP value");
        InitialCSValue = AddTwoBytes("Initial CS value");
        RelocationTablePointer = AddTwoBytes("Relocation table pointer");
        OverlayNumber = AddTwoBytes("Overlay number");
        ReservedWords = AddEightBytes("Reserved words");
        OEMIdentifier = AddTwoBytes("OEM Identifier");
        OEMInformation = AddTwoBytes("OEM Information");
        OtherReservedWords = AddBytes(20, "Other reserved words");
        CoffHeaderPointer = AddFourBytes("COFF Header pointer");
    }

    public TwoBytes MZSignature { get; set; }
    public TwoBytes ExtraPageSize { get; set; }
    public TwoBytes NumberOfPages { get; set; }
    public TwoBytes Relocations { get; set; }
    public TwoBytes HeaderSizeInParagraphs { get; set; }
    public TwoBytes MinimumAllocatedParagraphs { get; set; }
    public TwoBytes MaximumAllocatedParagraphs { get; set; }
    public TwoBytes InitialSSValue { get; set; }
    public TwoBytes InitialRelativeSPValue { get; set; }
    public TwoBytes Checksum { get; set; }
    public TwoBytes InitialRelativeIPValue { get; set; }
    public TwoBytes InitialCSValue { get; set; }
    public TwoBytes RelocationTablePointer { get; set; }
    public TwoBytes OverlayNumber { get; set; }
    public EightBytes ReservedWords { get; set; }
    public TwoBytes OEMIdentifier { get; set; }
    public TwoBytes OEMInformation { get; set; }
    public Node OtherReservedWords { get; set; }
    public FourBytes CoffHeaderPointer { get; set; }
}
