using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GuiLabs.FileFormat.PE.Dotnet;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile : Node
{
    public string FilePath { get; set; }

    public PEFile(ByteBuffer buffer)
    {
        Buffer = buffer;
    }

    public static PEFile ReadFromFile(string filePath, bool inMemory = true)
    {
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    public bool IsPE32Plus => OptionalHeader.StandardFields.IsPE32Plus;

    private bool HasValidPESignatures()
    {
        if (Buffer.Length < 0x40 || Buffer.ReadUInt16(0) != 0x5A4D)
        {
            return false;
        }

        int peHeaderPointer = Buffer.ReadInt32(0x3C);
        if (peHeaderPointer == 0)
        {
            peHeaderPointer = 0x80;
        }

        if (peHeaderPointer < 0 || peHeaderPointer + 4 > Buffer.Length)
        {
            return false;
        }

        return Buffer.ReadUInt32(peHeaderPointer) == 0x00004550;
    }

    public override void Parse()
    {
        if (!HasValidPESignatures())
        {
            int unknownLength = (int)System.Math.Min(Buffer.Length, int.MaxValue);
            Text = "Not a PE file";
            if (unknownLength > 0)
            {
                Add(new Unknown { Length = unknownLength, Text = "Not a PE file" });
            }

            return;
        }

        DOSHeader = Add<DOSHeader>("DOS Header");

        int peHeaderPointer = DOSHeader.CoffHeaderPointer.Value;
        if (peHeaderPointer == 0)
        {
            peHeaderPointer = 0x80;
        }

        // Find and parse Rich header between DOS header and PE header
        int richHeaderStart = -1;
        int richHeaderEnd = -1;
        for (int i = DOSHeader.End; i < peHeaderPointer - 7; i++)
        {
            if (Buffer.ReadByte(i) == 0x52 && // 'R'
                Buffer.ReadByte(i + 1) == 0x69 && // 'i'
                Buffer.ReadByte(i + 2) == 0x63 && // 'c'
                Buffer.ReadByte(i + 3) == 0x68)    // 'h'
            {
                richHeaderEnd = i + 8; // "Rich" + 4-byte XOR key
                uint xorKey = Buffer.ReadUInt32(i + 4);
                uint dansXored = 0x536E6144 ^ xorKey; // "DanS" XOR'd
                for (int j = i - 4; j >= DOSHeader.End; j -= 4)
                {
                    if (Buffer.ReadUInt32(j) == dansXored)
                    {
                        richHeaderStart = j;
                        break;
                    }
                }
                break;
            }
        }

        // DOS stub fills the gap between DOS header and Rich header (or PE header)
        int dosStubEnd = richHeaderStart > 0 ? richHeaderStart : peHeaderPointer;
        if (dosStubEnd > DOSHeader.End)
        {
            var DOSStub = new Node { Length = dosStubEnd - DOSHeader.End, Text = "DOS Stub" };
            Add(DOSStub);
        }

        // Add Rich header if found
        if (richHeaderStart > 0 && richHeaderEnd > richHeaderStart)
        {
            var richHeader = new RichHeader
            {
                Start = richHeaderStart,
                Length = richHeaderEnd - richHeaderStart
            };
            Add(richHeader);
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

        // Add remaining sections that weren't explicitly added
        var allSections = new List<Node>();
        if (TextSection != null) allSections.Add(TextSection);
        if (RsrcSection != null) allSections.Add(RsrcSection);
        if (RelocSection != null) allSections.Add(RelocSection);
        foreach (var sectionHeader in SectionTable.SectionHeaders)
        {
            var name = sectionHeader.Name.Text;
            if (name == ".text" || name == ".rsrc" || name == ".reloc")
                continue;
            if (sectionHeader.PointerToRawData.Value == 0 || sectionHeader.SizeOfRawData.Value == 0)
                continue;
            var section = new Section
            {
                Start = sectionHeader.PointerToRawData.Value,
                Length = sectionHeader.SizeOfRawData.Value,
                Text = name
            };
            Add(section);
            allSections.Add(section);
        }

        var firstSection = allSections.OrderBy(s => s.Start).FirstOrDefault();
        if (firstSection != null)
        {
            var sectionGap = new Span(SectionTable.End, firstSection.Start - SectionTable.End);
            if (sectionGap.Length > 0)
            {
                if (Buffer.IsZeroFilled(sectionGap))
                {
                    SectionTable.AddPadding(sectionGap.Length);
                }
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
            Add(DebugDirectories);

            foreach (var debugDirectory in DebugDirectories.Directories)
            {
                var address = debugDirectory.AddressOfRawData.Value;
                var start = address != 0
                    ? ResolveVirtualAddress(address)
                    : debugDirectory.PointerToRawData.Value;
                int size = debugDirectory.SizeOfData.Value;
                if (start > 0 && size > 0 && (long)start + size <= Buffer.Length)
                {
                    Node entry = debugDirectory.CreateDataNode(Buffer, start, size);
                    entry.Start = start;
                    if (entry is RSDS rsds)
                    {
                        RSDS = rsds;
                    }
                    else
                    {
                        entry.Length = size;
                    }

                    if (entry is EmbeddedPdb embeddedPdb)
                    {
                        EmbeddedPdb = embeddedPdb;
                    }

                    Add(entry);
                }
            }
        }

        ResourceTable = AddTable<ResourceTable>(OptionalHeader.DataDirectories.ResourceTable, text: "Resource table");

        ImportTable = AddTable<ImportTable>(OptionalHeader.DataDirectories.ImportTable, text: "Import table");

        ParseImportTable();

        ImportAddressTable = AddTable<IAT>(OptionalHeader.DataDirectories.IAT, configure: iat => iat.IsPE32Plus = IsPE32Plus);
        NameImportAddressTableEntries();

        // The startup stub is only present in managed images; a native image's
        // entry point is ordinary code
        if (CLIHeader != null &&
            OptionalHeader.StandardFields.AddressOfEntryPoint is { } entrypointBytes &&
            entrypointBytes.Value is int entrypointRVA &&
            entrypointRVA != 0)
        {
            int entryPointOffset = ResolveVirtualAddress(entrypointRVA);
            if (entryPointOffset > 0)
            {
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

                // In mixed-mode (C++/CLI) assemblies, the entry point may resolve
                // to an offset already claimed by a parsed method body. Only add
                // the stub if the location is not already deeply occupied.
                var found = Find(entryPointOffset);
                if (found == null || found == this || found.Parent == this)
                {
                    Add(runtimeStartupStub);
                }
            }
        }

        AddTable<BaseRelocationTable>(OptionalHeader.DataDirectories.BaseRelocationTable);
        ReadRelocations();
        AddTable<BoundImport>(OptionalHeader.DataDirectories.BoundImport, text: "Bound import");
        AddTable<DelayLoadImportTable>(OptionalHeader.DataDirectories.DelayImportDescriptor, configure: d =>
        {
            d.IsPE32Plus = IsPE32Plus;
            d.PEFile = this;
        });
        AddCertificateTable(OptionalHeader.DataDirectories.CertificateTable);
        ExceptionTable = AddTable<ExceptionTable>(OptionalHeader.DataDirectories.ExceptionTable, configure: et =>
        {
            et.IsPE32Plus = IsPE32Plus;
            et.MachineType = PEHeader.Platform.Value;
            et.PEFile = this;
            et.IsManaged = IsManagedFunctionTable(OptionalHeader.DataDirectories.ExceptionTable.RVA.Value);
        });
        AddReadyToRunSections();
        AddNativeImageSections();
        AddTable<ExportTable>(OptionalHeader.DataDirectories.ExportTable, configure: et =>
        {
            et.PEFile = this;
        });
        AddTable<LoadConfigDirectory>(OptionalHeader.DataDirectories.LoadConfigTable, configure: lc =>
        {
            lc.IsPE32Plus = IsPE32Plus;
            lc.PEFile = this;
        });
        AddTable<TLSDirectory>(OptionalHeader.DataDirectories.TLSTable, configure: tls =>
        {
            tls.IsPE32Plus = IsPE32Plus;
            tls.PEFile = this;
        });

        AddExceptionHandlerData(ExceptionTable);

        AddCoffSymbolTable();

        ReadSingleFileBundle();

        AddOverlay();

        AddNativeAotModules();
        AddNativeAotUnwindData(ExceptionTable);
        AddRtti();
        AddGcInfo();
        AddNativeImageVirtualSections();

        AddNativeCode(allSections);

        foreach (var section in allSections)
        {
            section.AddRemainingPadding();
        }

        this.ValidateOverlap((prev, current, parent) =>
        {
            throw new System.Exception(
                $"Overlap in {parent.Describe()}:\n" +
                $"  Previous: {prev.Describe()}\n" +
                $"  Current:  {current.Describe()}\n" +
                $"  Overlap:  {prev.End - current.Start} bytes");
        });

        ClassifyUncoveredSpans();

        Text = $"PE File";
    }

    private static readonly byte[] PaddingPattern = "PADDINGXXPADDING"u8.ToArray();

    // SHA-256 of ".net core bundle" (32 bytes)
    private static readonly byte[] BundleSignature = new byte[]
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    };

    private void ReadSingleFileBundle()
    {
        long fileLength = Buffer.Length;

        // The bundle signature is embedded in the apphost, typically in the .text section
        // Search the PE portion of the file for it
        int searchEnd = (int)System.Math.Min(fileLength - BundleSignature.Length, 1024 * 1024);

        int markerOffset = -1;
        for (int i = searchEnd; i >= 0; i--)
        {
            bool match = true;
            for (int j = 0; j < BundleSignature.Length; j++)
            {
                if (Buffer.ReadByte(i + j) != BundleSignature[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                markerOffset = i;
                break;
            }
        }

        if (markerOffset < 0)
        {
            return;
        }

        // The 8-byte header offset is right before the signature
        int markerStart = markerOffset - 8;
        if (markerStart < 0)
        {
            return;
        }

        // Validate the header offset: must be non-zero and within the file.
        // Non-bundle apphosts have zero here (the placeholder is all zeros).
        // Node offsets are 32-bit, so anything past 2 GB can't be represented.
        long headerOffset = (long)Buffer.ReadUInt64(markerStart);
        if (headerOffset <= 0 || headerOffset >= fileLength || headerOffset > int.MaxValue)
        {
            return;
        }

        // Extend PEFile to cover the entire file
        Length = (int)System.Math.Min(fileLength, int.MaxValue);
        var bundleMarker = new BundleMarker
        {
            Start = markerStart
        };
        Add(bundleMarker);

        var bundle = new SingleFileBundle
        {
            Start = (int)headerOffset
        };
        Add(bundle);

        // Add nodes for each bundled file's data
        foreach (var entry in bundle.Entries)
        {
            long entryOffset = (long)entry.Offset.ReadUInt64();
            long entrySize = (long)entry.Size.ReadUInt64();
            long compressedSize = entry.CompressedSize is { } compressed ? (long)compressed.ReadUInt64() : 0;
            if (compressedSize > 0)
            {
                entrySize = compressedSize;
            }

            if (entrySize > 0 && entryOffset > 0 && entryOffset + entrySize <= System.Math.Min(fileLength, int.MaxValue))
            {
                var bundledFile = new BundledFile
                {
                    Start = (int)entryOffset,
                    Length = (int)entrySize,
                    Text = entry.Text,
                    Entry = entry
                };
                entry.BundledFile = bundledFile;
                Add(bundledFile);
            }
        }
    }

    private void AddCoffSymbolTable()
    {
        int pointer = PEHeader.PointerToSymbolTable.Value;
        int symbolCount = PEHeader.NumberOfSymbols.Value;
        if (pointer <= 0 || symbolCount <= 0)
        {
            return;
        }

        const int symbolSize = 18;
        long tableEnd = (long)pointer + (long)symbolCount * symbolSize;
        if (tableEnd + 4 > Buffer.Length)
        {
            return;
        }

        var symbolTable = new CoffSymbolTable
        {
            Start = pointer,
            Length = symbolCount * symbolSize,
            SymbolCount = symbolCount
        };
        Add(symbolTable);

        // The string table immediately follows; its leading 4-byte size includes itself
        int stringTableStart = (int)tableEnd;
        int stringTableSize = Buffer.ReadInt32(stringTableStart);
        if (stringTableSize >= 4 && stringTableStart + stringTableSize <= Buffer.Length)
        {
            var stringTable = new CoffStringTable
            {
                Start = stringTableStart,
                Length = stringTableSize
            };
            Add(stringTable);
            symbolTable.StringTable = stringTable;
            symbolTable.ApplyLongNames();
        }
    }

    private void AddOverlay()
    {
        long fileLength = System.Math.Min(Buffer.Length, int.MaxValue);
        int end = End;
        if (fileLength > end)
        {
            var overlay = new Overlay
            {
                Start = end,
                Length = (int)fileLength - end
            };
            Add(overlay);
        }
    }

    private bool IsPaddingFilled(Span span)
    {
        if (span.Length < PaddingPattern.Length)
        {
            return false;
        }

        var bytes = Buffer.ReadBytes(span.Start, System.Math.Min(span.Length, PaddingPattern.Length));
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != PaddingPattern[i])
            {
                return false;
            }
        }

        return true;
    }

    private void AddNativeCode(IEnumerable<Node> sections)
    {
        const uint ImageScnCntCode = 0x00000020;

        foreach (var section in sections)
        {
            var sectionHeader = SectionTable.SectionHeaders.FirstOrDefault(s => s.PointerToRawData.Value == section.Start);
            if (sectionHeader == null || ((uint)sectionHeader.Characteristics.Value & ImageScnCntCode) == 0)
            {
                continue;
            }

            // The code section of a precompiled managed image also holds
            // metadata and runtime tables, and its code is listed by the
            // function table and by named regions. Only those are code.
            bool isManagedImage = ReadyToRunHeader != null || NativeImageHeader != null;
            int contentLength = Math.Min(section.Length, sectionHeader.VirtualSize.Value);
            AddNativeCode(section, section.Start + contentLength, fillGaps: !isManagedImage, isManagedImage);
        }
    }

    private void AddNativeCode(Node container, int contentEnd, bool fillGaps, bool isManagedImage)
    {
        contentEnd = Math.Min(contentEnd, container.End);
        int start = container.Start;
        var spans = new List<Span>();

        foreach (var child in container.Children)
        {
            int end = Math.Min(child.Start, contentEnd);
            if (end > start && fillGaps)
            {
                spans.Add(new Span(start, end - start));
            }

            if (child is Region region && child.Start < contentEnd)
            {
                AddNativeCode(child, contentEnd, isManagedImage ? region.ContainsCode : fillGaps, isManagedImage);
            }

            start = Math.Max(start, child.End);
            if (start >= contentEnd)
            {
                break;
            }
        }

        if (start < contentEnd && fillGaps)
        {
            spans.Add(new Span(start, contentEnd - start));
        }

        foreach (var span in spans)
        {
            if (Buffer.IsZeroFilled(span))
            {
                continue;
            }

            if (IsCodePadding(span))
            {
                container.Add(new Padding { Start = span.Start, Length = span.Length });
            }
            else
            {
                container.Add(new NativeCode { Start = span.Start, Length = span.Length });
            }
        }
    }

    private void ReadDotnetMetadata(int cliHeader)
    {
        CLIHeader = new CLIHeader { Start = cliHeader };
        Add(CLIHeader);

        var metadataDirectory = CLIHeader.Metadata;
        MetadataRVA = metadataDirectory.RVA.Value;
        MetadataSectionHeader = GetSectionAtVirtualAddress(MetadataRVA);
        int metadata = ResolveDataDirectory(metadataDirectory);

        // The managed native header goes in first: in ReadyToRun and NGEN
        // images it is located between the CLI header and the metadata
        AddManagedNativeHeader();

        if (metadata > 0)
        {
            // In an IL-only image everything between the CLI header and the
            // metadata is method bodies. Precompiled images keep many other
            // structures there, so the grouping node is left out.
            bool isILOnly = ReadyToRunHeader == null && NativeImageHeader == null;
            IL il = null;
            if (isILOnly && metadata > CLIHeader.End)
            {
                il = new IL
                {
                    Start = CLIHeader.End,
                    Length = metadata - CLIHeader.End
                };
                Add(il);
            }

            Metadata = new Metadata { Start = metadata };
            Add(Metadata);

            il?.FillWithPadding();
        }

        AddTable<StrongNameSignature>(CLIHeader.StrongNameSignature);
        AddVTableFixups();
    }

    private void AddManagedNativeHeader()
    {
        int nativeHeaderRVA = CLIHeader.ManagedNativeHeader.ReadInt32();
        int nativeHeaderSize = Buffer.ReadInt32(CLIHeader.ManagedNativeHeader.Start + 4);
        if (nativeHeaderRVA == 0 || nativeHeaderSize < 16)
        {
            return;
        }

        int offset = ResolveVirtualAddress(nativeHeaderRVA);
        if (offset <= 0)
        {
            return;
        }

        const uint ReadyToRunSignature = 0x00525452;
        const uint NativeImageSignature = 0x0045474E;
        uint signature = Buffer.ReadUInt32(offset);
        if (signature == NativeImageSignature)
        {
            AddNativeImageHeader(offset, nativeHeaderSize);
            return;
        }

        if (signature != ReadyToRunSignature || !IsRangeAvailable(offset, nativeHeaderSize))
        {
            return;
        }

        ReadyToRunHeader = new ReadyToRunHeader
        {
            Start = offset,
            Length = nativeHeaderSize
        };
        Add(ReadyToRunHeader);
    }

    private void AddVTableFixups()
    {
        int rva = CLIHeader.VTableFixups.ReadInt32();
        int size = Buffer.ReadInt32(CLIHeader.VTableFixups.Start + 4);
        int offset = rva > 0 ? ResolveVirtualAddress(rva) : 0;
        if (offset <= 0 || size < 8 || !IsRangeAvailable(offset, size))
        {
            return;
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = size / 8 * 8,
            Count = size / 8,
            Fields = [("Slots RVA", 4), ("Slot count", 2), ("Type", 2)],
            RecordText = "VTable fixup"
        };
        Add(table);
        table.Text = "VTable fixups";

        foreach (var record in table.Children.OfType<Record>())
        {
            int slotsRva = record.ReadInt32(0);
            int count = Buffer.ReadUInt16(record.Children[1].Start);
            int type = Buffer.ReadUInt16(record.Children[2].Start);
            const int Is64Bit = 0x02;
            int slotSize = (type & Is64Bit) != 0 ? 8 : 4;
            int slotsOffset = ResolveVirtualAddress(slotsRva);
            if (slotsOffset <= 0 || count == 0)
            {
                continue;
            }

            var slots = new RecordTable
            {
                Start = slotsOffset,
                Length = count * slotSize,
                Count = count,
                Fields = [("Method token", slotSize)],
                RecordText = "VTable slot"
            };
            if (TryAdd(slots))
            {
                slots.Text = $"VTable fixup slots ({count})";
            }
        }
    }

    private Node AddCertificateTable(DataDirectory dataDirectory)
    {
        if (dataDirectory.Size.Value > 0)
        {
            // Unlike other data directories, the certificate table's "RVA"
            // field is a plain file offset
            var node = new CertificateTable
            {
                Start = dataDirectory.RVA.Value,
                Length = dataDirectory.Size.Value
            };

            Add(node);
            return node;
        }

        return null;
    }

    private T AddTable<T>(DataDirectory dataDirectory, bool isRVA = true, string text = null, Action<T> configure = null) where T : Node, new()
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

            configure?.Invoke(node);

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

        int entrySize = IsPE32Plus ? 8 : 4;

        for (int i = 0; i < list.Count - 1; i++)
        {
            var directory = list[i];
            var addressTableRva = directory.AddressTableRVA;
            var dllNameRva = directory.DllNameRVA;
            var lookupTableRva = directory.LookupTableRVA;

            var dllName = ResolveVirtualAddress(dllNameRva.Value);
            string dllNameText = ReadZeroTerminatedAscii(dllName) ?? "";
            if (dllNameText.Length > 0)
            {
                directory.Text = $"Imports directory: {dllNameText}";
            }

            var lookupTable = ResolveVirtualAddress(lookupTableRva.Value);

            if (lookupTable > 0)
            {
                var importLookupTable = new ImportLookupTable() { Start = lookupTable, IsPE32Plus = IsPE32Plus };
                ImportTable.Add(importLookupTable);
                importLookupTable.Text = $"Import Lookup Table: {dllNameText}";
                for (int j = 0; j < importLookupTable.Entries.Count - 1; j++)
                {
                    var entry = importLookupTable.Entries[j];
                    string name = AddImportName(entry.Start);
                    if (name != null)
                    {
                        entry.Text = name;
                        ImportNamesBySlotRva[addressTableRva.Value + j * entrySize] = $"{dllNameText}!{name}";
                    }
                }
            }
            else
            {
                // Some linkers leave the lookup table RVA zero; the loader
                // falls back to the address table, which holds the same entries
                int addressTable = ResolveVirtualAddress(addressTableRva.Value);
                if (addressTable > 0)
                {
                    int index = 0;
                    for (int offset = addressTable; offset + entrySize <= Buffer.Length; offset += entrySize, index++)
                    {
                        if (ReadPointer(offset) == 0)
                        {
                            break;
                        }

                        string name = AddImportName(offset);
                        if (name != null)
                        {
                            ImportNamesBySlotRva[addressTableRva.Value + index * entrySize] = $"{dllNameText}!{name}";
                        }
                    }
                }
            }

            if (dllName > 0)
            {
                var dllNameNode = new ZeroTerminatedString() { Start = dllName };
                ImportTable.Add(dllNameNode);
            }
        }
    }

    /// <summary>
    /// Import names keyed by the RVA of the import address table slot that
    /// receives the address.
    /// </summary>
    public Dictionary<int, string> ImportNamesBySlotRva { get; } = new();

    private void NameImportAddressTableEntries()
    {
        if (ImportAddressTable?.Entries == null)
        {
            return;
        }

        int entrySize = IsPE32Plus ? 8 : 4;
        int tableRva = OptionalHeader.DataDirectories.IAT.RVA.Value;
        for (int i = 0; i < ImportAddressTable.Entries.Count; i++)
        {
            if (ImportNamesBySlotRva.TryGetValue(tableRva + i * entrySize, out var name))
            {
                ImportAddressTable.Entries[i].Text = name;
            }
        }
    }

    private string AddImportName(int entryOffset)
    {
        // For PE32+, the ordinal flag is bit 63; for PE32 it's bit 31
        long value = IsPE32Plus
            ? (long)Buffer.ReadUInt64(entryOffset)
            : (long)Buffer.ReadUInt32(entryOffset);
        if (value == 0)
        {
            return null;
        }

        // High bit set means import by ordinal, not by name
        bool isOrdinal = IsPE32Plus ? (value & unchecked((long)0x8000000000000000)) != 0 : (value & 0x80000000) != 0;
        if (isOrdinal)
        {
            return $"Ordinal {value & 0xFFFF}";
        }

        int rva = (int)(value & 0x7FFFFFFF);
        var resolved = ResolveVirtualAddress(rva);
        if (resolved > 0)
        {
            var imageImportByName = new ImageImportByName() { Start = resolved };
            ImportTable.Add(imageImportByName);
            imageImportByName.Text = imageImportByName.Name.Text;
            return imageImportByName.Name.Text;
        }

        return null;
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

        // An RVA in the virtual-only tail of a section (VirtualSize > SizeOfRawData)
        // has no backing file data
        if (rva - section.VirtualAddress.Value >= section.SizeOfRawData.Value)
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
            int virtualExtent = Math.Max(section.VirtualSize.Value, section.SizeOfRawData.Value);
            if (rva >= virtualAddress && rva < virtualAddress + virtualExtent)
            {
                return section;
            }
        }

        return null;
    }

    public bool IsRangeAvailable(int start, int length)
    {
        if (start < 0 || length <= 0 || start > Buffer.Length - length)
        {
            return false;
        }

        int end = start + length;
        int sectionIndex = FindChildIndex(start);
        if (sectionIndex < 0 || Children[sectionIndex] is not Section section || end > section.End)
        {
            return false;
        }

        return FindAvailableParent(section, start, end) != null;
    }

    private static Node FindAvailableParent(Node parent, int start, int end)
    {
        while (parent.HasChildren)
        {
            int index = parent.FindChildIndex(start);
            if (index >= 0)
            {
                var child = parent.Children[index];
                if (end <= child.End && child.HasChildren)
                {
                    parent = child;
                    continue;
                }

                return null;
            }

            index = ~index;
            if (index < parent.Children.Count && parent.Children[index].Start < end)
            {
                return null;
            }

            break;
        }

        return parent;
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
