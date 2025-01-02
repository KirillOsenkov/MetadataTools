using System.Linq;
using GuiLabs.FileFormat.PE.Dotnet;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat.PE;

public class PEFile : Node
{
    public PEFile(ByteBuffer buffer)
    {
        Buffer = buffer;
    }

    public override void Parse()
    {
        DOSHeaderAndStub = new Node() { Text = "DOS Header and Stub" };
        Add(DOSHeaderAndStub);

        var DOSHeader = new Node { Length = 0x3C, Text = "DOS Header" };
        DOSHeaderAndStub.Add(DOSHeader);

        PEHeaderPointer = DOSHeaderAndStub.AddFourBytes("PE Header Pointer");

        var DOSStub = new Node { Length = 0x40, Text = "DOS Stub" };
        DOSHeaderAndStub.Add(DOSStub);

        int peHeaderPointer = PEHeaderPointer.Value;
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

        ResourceTable = AddTable<Node>(OptionalHeader.DataDirectories.ResourceTable, text: "Resource table");

        ImportTable = AddTable<ImportTable>(OptionalHeader.DataDirectories.ImportTable, text: "Import table");

        if (OptionalHeader.StandardFields.AddressOfEntryPoint is { } entrypointBytes &&
            entrypointBytes.Value is int entrypointRVA &&
            entrypointRVA != 0)
        {
            int entryPointOffset = ResolveVirtualAddress(entrypointRVA);
            RuntimeStartupStub runtimeStartupStub = null;
            if (OptionalHeader.StandardFields.IsPE32Plus)
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
            var gap = new Span(ImportTable.End, runtimeStartupStub.Start - ImportTable.End);
            if (gap.Length > 0 && Buffer.IsZeroFilled(gap))
            {
                Add(new Padding { Start = gap.Start, Length = gap.Length });
            }
        }

        AddTable<Node>(OptionalHeader.DataDirectories.BaseRelocationTable, text: "Base reloc table");
        AddTable<BoundImport>(OptionalHeader.DataDirectories.BoundImport, text: "Bound import");
        AddCertificateTable(OptionalHeader.DataDirectories.CertificateTable);
        AddTable<Node>(OptionalHeader.DataDirectories.ExceptionTable, text: "Exception table");
        AddTable<Node>(OptionalHeader.DataDirectories.ExportTable, text: "Export table");
        AddTable<Node>(OptionalHeader.DataDirectories.LoadConfigTable, text: "Load config table");
        AddTable<Node>(OptionalHeader.DataDirectories.TLSTable, text: "Thread Local Storage table");
        AddTable<IAT>(OptionalHeader.DataDirectories.IAT);

        TextSection.AddRemainingPadding();
        RsrcSection.AddRemainingPadding();
        RelocSection.AddRemainingPadding();

        Text = $"PE File ({Length:N0} bytes)";
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

            il.ComputeUncoveredSpans(span =>
            {
                if (Buffer.IsZeroFilled(span))
                {
                    var padding = new Padding
                    {
                        Start = span.Start,
                        Length = span.Length
                    };
                    il.Add(padding);
                }
            });
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

    public Node DOSHeaderAndStub { get; set; }
    public FourBytes PEHeaderPointer { get; set; }
    public PEHeader PEHeader { get; set; }
    public OptionalHeader OptionalHeader { get; set; }
    public SectionTable SectionTable { get; set; }
    public CLIHeader CLIHeader { get; set; }
    public Metadata Metadata { get; set; }
    public SectionHeader MetadataSectionHeader { get; set; }
    public int MetadataRVA { get; set; }
    public DebugDirectories DebugDirectories { get; set; }
    public EmbeddedPdb EmbeddedPdb { get; set; }
    public Node ResourceTable { get; set; }
    public Node TextSection { get; set; }
    public Node RsrcSection { get; set; }
    public Node RelocSection { get; set; }
    public ImportTable ImportTable { get; set; }

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
