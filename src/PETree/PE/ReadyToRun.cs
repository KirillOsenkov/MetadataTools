using System;
using System.Collections.Generic;
using System.Linq;
using GuiLabs.FileFormat.PE.Dotnet;

namespace GuiLabs.FileFormat.PE;

public partial class PEFile
{
    public ReadyToRunHeader ReadyToRunHeader { get; set; }
    public NativeImageHeader NativeImageHeader { get; set; }

    private readonly List<int> readyToRunSignatureRvas = new();
    private bool readyToRunSignaturesHaveRefMaps;

    /// <summary>
    /// True when the exception directory at this RVA is the function table of
    /// precompiled managed code.
    /// </summary>
    private bool IsManagedFunctionTable(int rva)
    {
        if (NativeImageHeader != null)
        {
            return true;
        }

        var sections = ReadyToRunHeader?.Sections;
        if (sections == null)
        {
            return false;
        }

        foreach (var section in sections)
        {
            if (section.SectionType.Value == 102 && section.RVA.Value == rva)
            {
                return true;
            }
        }

        return false;
    }

    private void AddReadyToRunSections()
    {
        var sections = ReadyToRunHeader?.Sections;
        if (sections == null)
        {
            return;
        }

        foreach (var section in sections)
        {
            AddReadyToRunSection(section.SectionType.Value, section.RVA.Value, section.Size.Value, depth: 0);
        }

        AddReadyToRunSignatures();
    }

    private static readonly (string, int)[] importSectionFields =
    [
        ("Section RVA", 4),
        ("Section size", 4),
        ("Flags", 2),
        ("Type", 1),
        ("Entry size", 1),
        ("Signatures RVA", 4),
        ("Auxiliary data RVA", 4)
    ];

    private static readonly (string, int)[] exceptionClauseFields =
    [
        ("Flags", 4),
        ("Try start", 4),
        ("Try end", 4),
        ("Handler start", 4),
        ("Handler end", 4),
        ("Class token or filter offset", 4)
    ];

    private void AddReadyToRunSection(int type, int rva, int size, int depth)
    {
        int offset = ResolveVirtualAddress(rva);
        if (offset <= 0 || size <= 0 || (long)offset + size > Buffer.Length)
        {
            return;
        }

        string text = $"R2R {ReadyToRunSection.GetSectionTypeName(type)}";
        Node node;
        switch (type)
        {
            case 100:
            case 116:
                node = new Utf8String();
                break;
            case 101:
                node = new RecordTable { Count = size / 20, Fields = importSectionFields, RecordText = "Import section" };
                break;
            case 102:
                if (ExceptionTable != null && ExceptionTable.Start == offset)
                {
                    ExceptionTable.Text = "Exception table (R2R RuntimeFunctions)";
                    return;
                }

                node = new ExceptionTable
                {
                    MachineType = PEHeader.Platform.Value,
                    IsPE32Plus = IsPE32Plus,
                    PEFile = this,
                    IsManaged = true
                };
                break;
            case 103:
                node = new NativeArray { DescribeElement = DescribeMethodEntryPoint };
                break;
            case 105:
                node = new NativeArray { DescribeElement = (index, _, _) => $"Debug info of MethodDef 0x{0x06000001 + index:X8}" };
                break;
            case 104:
                node = new RecordTable
                {
                    Count = size / 8,
                    Fields = [("Method start RVA", 4), ("Exception info RVA", 4)],
                    RecordText = "Exception lookup entry"
                };
                break;
            case 106:
                node = new NativeCode();
                break;
            case 108:
            case 109:
            case 114:
            case 117:
            case 119:
                node = new NativeHashtable();
                break;
            case 112:
                AddSecondaryMetadata(offset, text);
                return;
            case 113:
                node = new RecordTable { Count = size / 4, Fields = [("Bits", 4)], RecordText = "Bloom filter word" };
                break;
            case 115:
                node = new RecordTable
                {
                    Count = size / 16,
                    Fields =
                    [
                        ("COR header RVA", 4),
                        ("COR header size", 4),
                        ("ReadyToRun core header RVA", 4),
                        ("ReadyToRun core header size", 4)
                    ],
                    RecordText = "Component assembly"
                };
                break;
            case 118:
                node = new RecordTable { Count = size / 16, Fields = [("MVID", 16)], RecordText = "Assembly MVID" };
                break;
            case 120:
                node = new RecordTable
                {
                    Count = size / 8,
                    Fields = [("Cold runtime function index", 4), ("Hot runtime function index", 4)],
                    RecordText = "Split method"
                };
                break;
            case 121:
            case 123:
                node = size > 4
                    ? new Record { Fields = [("Entry count", 4), ("Entries", size - 4)] }
                    : new Node();
                break;
            case 122:
                node = size > 2
                    ? new Record { Fields = [("Entry count", 2), ("Enclosing type RIDs", size - 2)] }
                    : new Node();
                break;
            default:
                node = new Node();
                break;
        }

        node.Start = offset;
        node.Length = size;
        if (!TryAdd(node))
        {
            TryAddRegion(new Region { Start = offset, Length = size, Text = text });
            return;
        }

        switch (type)
        {
            case 100:
            case 116:
                node.Text = $"{text}: {System.Text.Encoding.UTF8.GetString(Buffer.ReadBytes(offset, size)).TrimEnd('\0')}";
                break;
            case 101:
                node.Text = text;
                AddReadyToRunImportSections((RecordTable)node);
                break;
            case 103:
            case 105:
                node.Text = $"{text} ({((NativeArray)node).ElementCount} elements)";
                break;
            case 108:
            case 109:
            case 114:
            case 117:
            case 119:
                node.Text = $"{text} ({((NativeHashtable)node).EntryCount} entries)";
                break;
            case 104:
                node.Text = text;
                AddReadyToRunExceptionClauses((RecordTable)node);
                break;
            case 115:
                node.Text = text;
                if (depth == 0)
                {
                    AddReadyToRunComponentAssemblies((RecordTable)node);
                }

                break;
            default:
                node.Text = text;
                break;
        }
    }

    /// <summary>
    /// An entry point element starts with an integer that holds the index of
    /// the method in the runtime function table and says whether a list of
    /// import slots to fill first follows.
    /// </summary>
    private string DescribeMethodEntryPoint(int index, int offset, int length)
    {
        int size = FieldCursor.MeasureCompressed(Buffer, offset);
        if (size == 0)
        {
            return $"MethodDef 0x{0x06000001 + index:X8}";
        }

        uint value = FieldCursor.ReadCompressedUInt32(Buffer, offset, size);
        string fixups = "";
        uint functionIndex;
        if ((value & 1) == 0)
        {
            functionIndex = value >> 1;
        }
        else
        {
            functionIndex = value >> 2;
            fixups = (value & 2) != 0 ? ", reference to a fixup list" : ", fixup list";
        }

        return $"MethodDef 0x{0x06000001 + index:X8}: runtime function {functionIndex}{fixups}";
    }

    private void AddSecondaryMetadata(int offset, string text)
    {
        if (!IsRangeAvailable(offset, 16) || Buffer.ReadUInt32(offset) != Metadata.BSJBSignature)
        {
            return;
        }

        var metadata = new Metadata { Start = offset, IsSecondary = true };
        Add(metadata);
        metadata.Text = text;
    }

    private static string GetImportSectionTypeName(int type)
    {
        return type switch
        {
            0 => "Unknown",
            1 => "ExternalMethod",
            2 => "StubDispatch",
            3 => "StringHandle",
            4 => "TypeHandle",
            5 => "MethodHandle",
            6 => "VirtualMethod",
            7 => "ILBodyFixups",
            _ => $"Type {type}"
        };
    }

    /// <summary>
    /// Adds what each import section record points at: the slots, the
    /// parallel array of signature RVAs, and the GC reference maps.
    /// </summary>
    private void AddReadyToRunImportSections(RecordTable table)
    {
        foreach (var record in table.Children.OfType<Record>())
        {
            int sectionRva = record.ReadInt32(0);
            int sectionSize = record.ReadInt32(1);
            int flags = Buffer.ReadUInt16(record.Children[2].Start);
            int type = Buffer.ReadByte(record.Children[3].Start);
            int entrySize = Buffer.ReadByte(record.Children[4].Start);
            int signaturesRva = record.ReadInt32(5);
            int auxiliaryRva = record.ReadInt32(6);
            if (entrySize == 0)
            {
                entrySize = PointerSize;
            }

            int count = sectionSize / entrySize;
            string typeName = GetImportSectionTypeName(type);
            var flagNames = new List<string>();
            if ((flags & 1) != 0)
            {
                flagNames.Add("eager");
            }

            if ((flags & 4) != 0)
            {
                flagNames.Add("code pointers");
            }

            if ((flags & 8) != 0)
            {
                flagNames.Add("GC ref maps in signatures");
                readyToRunSignaturesHaveRefMaps = true;
            }

            string flagText = flagNames.Count == 0 ? "" : $", {string.Join(", ", flagNames)}";
            record.Text = $"Import section: {typeName}, {count} slots{flagText}";

            int slotsOffset = ResolveVirtualAddress(sectionRva);
            if (slotsOffset > 0 && count > 0)
            {
                var slots = new RecordTable
                {
                    Start = slotsOffset,
                    Length = count * entrySize,
                    Count = count,
                    Fields = [("Slot", entrySize)],
                    RecordText = "Slot"
                };
                if (TryAdd(slots))
                {
                    slots.Text = $"Import slots: {typeName} ({count})";
                }
            }

            int signaturesOffset = signaturesRva > 0 ? ResolveVirtualAddress(signaturesRva) : 0;
            if (signaturesOffset > 0 && count > 0 && (long)signaturesOffset + count * 4L <= Buffer.Length)
            {
                var signatures = new RecordTable
                {
                    Start = signaturesOffset,
                    Length = count * 4,
                    Count = count,
                    Fields = [("Signature RVA", 4)],
                    RecordText = "Signature RVA"
                };
                if (TryAdd(signatures))
                {
                    signatures.Text = $"Import signature RVAs: {typeName} ({count})";
                }

                for (int i = 0; i < count; i++)
                {
                    readyToRunSignatureRvas.Add(Buffer.ReadInt32(signaturesOffset + i * 4));
                }
            }

            int auxiliaryOffset = auxiliaryRva > 0 ? ResolveVirtualAddress(auxiliaryRva) : 0;
            if (auxiliaryOffset > 0 && count > 0)
            {
                AddGcRefMaps(auxiliaryOffset, count, typeName);
            }
        }
    }

    private void AddGcRefMaps(int offset, int count, string typeName)
    {
        // A lookup table with one entry per 1024 maps, then one map per slot.
        // Each map ends with a byte whose high bit is clear.
        int lookupSize = (count / 1024 + 1) * 4;
        long position = (long)offset + lookupSize;
        for (int i = 0; i < count; i++)
        {
            while (position < Buffer.Length && (Buffer.ReadByte((int)position) & 0x80) != 0)
            {
                position++;
            }

            position++;
            if (position > Buffer.Length)
            {
                return;
            }
        }

        var maps = new Record
        {
            Start = offset,
            Length = (int)(position - offset),
            Fields = [("Lookup table", lookupSize), ("Maps", (int)(position - offset) - lookupSize)]
        };
        if (TryAdd(maps))
        {
            maps.Text = $"GC reference maps: {typeName} ({count})";
        }
    }

    /// <summary>
    /// Fixup signatures have no length field. They are stored one after
    /// another, so each one is taken to run up to the next referenced RVA.
    /// </summary>
    private void AddReadyToRunSignatures()
    {
        var rvas = readyToRunSignatureRvas.Where(r => r > 0).Distinct().OrderBy(r => r).ToArray();
        for (int i = 0; i < rvas.Length; i++)
        {
            int offset = ResolveVirtualAddress(rvas[i]);
            if (offset <= 0)
            {
                continue;
            }

            int length = GetFreeLength(offset, 1024);
            if (i + 1 < rvas.Length)
            {
                length = Math.Min(length, rvas[i + 1] - rvas[i]);
            }
            else
            {
                length = Math.Min(length, 1);
            }

            if (length <= 0)
            {
                continue;
            }

            int kindOffset = offset;
            if (readyToRunSignaturesHaveRefMaps)
            {
                while (kindOffset < offset + length - 1 && (Buffer.ReadByte(kindOffset) & 0x80) != 0)
                {
                    kindOffset++;
                }

                kindOffset++;
            }

            byte kind = kindOffset < offset + length ? Buffer.ReadByte(kindOffset) : (byte)0;
            Add(new ReadyToRunSignature
            {
                Start = offset,
                Length = length,
                Text = $"Fixup signature: {GetFixupKindName(kind)}"
            });
        }
    }

    /// <summary>
    /// Number of bytes at the offset, up to max, that are inside a section or
    /// region and not claimed by a node.
    /// </summary>
    public int GetFreeLength(int offset, int max)
    {
        var container = FindDeepest(offset);
        if (container == null || (container is not Section && container is not Region))
        {
            return 0;
        }

        int index = container.FindChildIndex(offset);
        if (index >= 0)
        {
            return 0;
        }

        index = ~index;
        int end = container.HasChildren && index < container.Children.Count ? container.Children[index].Start : container.End;
        return Math.Min(end - offset, max);
    }

    public static string GetFixupKindName(int kind)
    {
        string name = (kind & 0x7F) switch
        {
            0x07 => "ThisObjDictionaryLookup",
            0x08 => "TypeDictionaryLookup",
            0x09 => "MethodDictionaryLookup",
            0x10 => "TypeHandle",
            0x11 => "MethodHandle",
            0x12 => "FieldHandle",
            0x13 => "MethodEntry",
            0x14 => "MethodEntry_DefToken",
            0x15 => "MethodEntry_RefToken",
            0x16 => "VirtualEntry",
            0x17 => "VirtualEntry_DefToken",
            0x18 => "VirtualEntry_RefToken",
            0x19 => "VirtualEntry_Slot",
            0x1A => "Helper",
            0x1B => "StringHandle",
            0x1C => "NewObject",
            0x1D => "NewArray",
            0x1E => "IsInstanceOf",
            0x1F => "ChkCast",
            0x20 => "FieldAddress",
            0x21 => "CctorTrigger",
            0x22 => "StaticBaseNonGC",
            0x23 => "StaticBaseGC",
            0x24 => "ThreadStaticBaseNonGC",
            0x25 => "ThreadStaticBaseGC",
            0x26 => "FieldBaseOffset",
            0x27 => "FieldOffset",
            0x28 => "TypeDictionary",
            0x29 => "MethodDictionary",
            0x2A => "Check_TypeLayout",
            0x2B => "Check_FieldOffset",
            0x2C => "DelegateCtor",
            0x2D => "DeclaringTypeHandle",
            0x2E => "IndirectPInvokeTarget",
            0x2F => "PInvokeTarget",
            0x30 => "Check_InstructionSetSupport",
            0x31 => "Verify_FieldOffset",
            0x32 => "Verify_TypeLayout",
            0x33 => "Check_VirtualFunctionOverride",
            0x34 => "Verify_VirtualFunctionOverride",
            0x35 => "Check_IL_Body",
            0x36 => "Verify_IL_Body",
            0x37 => "ContinuationLayout",
            0x38 => "ResumptionStubEntryPoint",
            0x39 => "InjectStringThunks",
            0x3A => "StoreMultiCallableAddrOfCode",
            _ => $"kind 0x{kind & 0x7F:X2}"
        };
        return (kind & 0x80) != 0 ? name + " (module override)" : name;
    }

    private void AddReadyToRunExceptionClauses(RecordTable table)
    {
        var records = table.Children.OfType<Record>().ToArray();
        for (int i = 0; i + 1 < records.Length; i++)
        {
            int methodRva = records[i].ReadInt32(0);
            int clausesRva = records[i].ReadInt32(1);
            int nextRva = records[i + 1].ReadInt32(1);
            int size = nextRva - clausesRva;
            int offset = ResolveVirtualAddress(clausesRva);
            if (offset <= 0 || size <= 0 || size % 24 != 0 || size > 24 * 4096)
            {
                continue;
            }

            var clauses = new RecordTable
            {
                Start = offset,
                Length = size,
                Count = size / 24,
                Fields = exceptionClauseFields,
                RecordText = "Exception clause"
            };
            if (TryAdd(clauses))
            {
                clauses.Text = $"Exception clauses of method 0x{methodRva:X} ({size / 24})";
            }
        }
    }

    private void AddReadyToRunComponentAssemblies(RecordTable table)
    {
        foreach (var record in table.Children.OfType<Record>())
        {
            int corHeaderRva = record.ReadInt32(0);
            int coreHeaderRva = record.ReadInt32(2);
            int coreHeaderSize = record.ReadInt32(3);

            int corHeaderOffset = corHeaderRva > 0 ? ResolveVirtualAddress(corHeaderRva) : 0;
            if (corHeaderOffset > 0 && IsRangeAvailable(corHeaderOffset, 72))
            {
                Add(new CLIHeader { Start = corHeaderOffset });
            }

            int coreHeaderOffset = coreHeaderRva > 0 ? ResolveVirtualAddress(coreHeaderRva) : 0;
            if (coreHeaderOffset <= 0 || coreHeaderSize < 8 || !IsRangeAvailable(coreHeaderOffset, coreHeaderSize))
            {
                continue;
            }

            var coreHeader = new ReadyToRunCoreHeader { Start = coreHeaderOffset, Length = coreHeaderSize };
            Add(coreHeader);
            coreHeader.Text = "ReadyToRun component core header";
            foreach (var section in coreHeader.Sections)
            {
                AddReadyToRunSection(section.SectionType.Value, section.RVA.Value, section.Size.Value, depth: 1);
            }
        }
    }

    /// <summary>
    /// In ReadyToRun images the GC info of a method is stored right after its
    /// unwind info and has no length field, so the bytes up to the next node
    /// are labelled as GC info.
    /// </summary>
    private void AddGcInfo()
    {
        if (ReadyToRunHeader == null)
        {
            return;
        }

        const int MaximumGcInfoSize = 0x10000;
        foreach (var unwindInfo in ManagedUnwindInfos)
        {
            var parent = unwindInfo.Parent;
            if (parent == null || (parent is not Section && parent is not Region))
            {
                continue;
            }

            int index = parent.FindChildIndex(unwindInfo.Start);
            if (index < 0)
            {
                continue;
            }

            int gapStart = unwindInfo.End;
            int gapEnd = index + 1 < parent.Children.Count ? parent.Children[index + 1].Start : parent.End;
            int length = gapEnd - gapStart;
            if (length > 0 && length <= MaximumGcInfoSize)
            {
                parent.Add(new GcInfo { Start = gapStart, Length = length });
            }
        }
    }

    /// <summary>
    /// Wraps a byte range of a section in a region node. Nodes that are
    /// already inside the range become children of the region. Fails when a
    /// node crosses the range boundary.
    /// </summary>
    public bool TryAddRegion(Region region)
    {
        int start = region.Start;
        int end = region.End;
        if (region.Length <= 0)
        {
            return false;
        }

        int sectionIndex = FindChildIndex(start);
        if (sectionIndex < 0 || Children[sectionIndex] is not Section section || end > section.End)
        {
            return false;
        }

        Node parent = section;
        while (true)
        {
            int index = parent.FindChildIndex(start);
            if (index < 0)
            {
                break;
            }

            var child = parent.Children[index];
            bool isSameRange = child.Start == start && child.End == end;
            if (child is Region && end <= child.End && !isSameRange)
            {
                parent = child;
                continue;
            }

            break;
        }

        var children = parent.Children;
        int first = parent.FindChildIndex(start);
        if (first < 0)
        {
            first = ~first;
        }
        else if (children[first].Start < start)
        {
            return false;
        }

        int last = first;
        while (last < children.Count && children[last].Start < end)
        {
            if (children[last].End > end)
            {
                return false;
            }

            last++;
        }

        region.Buffer = Buffer;
        region.Parent = parent;
        var moved = children.GetRange(first, last - first);
        children.RemoveRange(first, last - first);
        foreach (var node in moved)
        {
            node.Parent = region;
            region.Children.Add(node);
        }

        children.Insert(first, region);
        return true;
    }

    private void AddNativeImageHeader(int offset, int size)
    {
        int expectedSize = IsPE32Plus ? 168 : 164;
        if (size < expectedSize || !IsRangeAvailable(offset, expectedSize))
        {
            return;
        }

        NativeImageHeader = new NativeImageHeader { Start = offset, Length = expectedSize, IsPE32Plus = IsPE32Plus };
        Add(NativeImageHeader);
    }

    private static readonly string[] nativeImageSectionNames =
    [
        "(none)", "Module", "EETable", "WriteData", "WriteableData", "Data", "RVAStatics", "EEData",
        "DelayLoadInfoTableEager", "DelayLoadInfoTable", "EEReadonlyData", "ReadonlyData", "Class",
        "CrossDomainInfo", "MethodDesc", "MethodDescWriteable", "Exception", "Instrument",
        "VirtualImportThunk", "ExternalMethodThunk", "HelperTable", "MethodPrecodeWriteable",
        "MethodPrecodeWrite", "MethodPrecode", "Win32Resources", "Header", "Metadata", "DelayLoadInfo",
        "ImportTable", "Code", "CodeHeader", "CodeManager", "UnwindData", "RuntimeFunction", "Stubs",
        "StubDispatchData", "ExternalMethodData", "DelayLoadInfoDelayList", "ReadonlyShared", "Readonly",
        "IL", "GCInfo", "ILMetadata", "Resources", "CompressedMaps", "Debug", "BaseRelocs"
    ];

    /// <summary>
    /// Adds what the NGEN header directories point at. The table of virtual
    /// sections names every part of the image, so those ranges are added as
    /// regions around the nodes found so far.
    /// </summary>
    private void AddNativeImageSections()
    {
        var header = NativeImageHeader;
        if (header == null)
        {
            return;
        }

        foreach (var directory in header.Directories)
        {
            int offset = directory.RVA.Value > 0 ? ResolveVirtualAddress(directory.RVA.Value) : 0;
            int size = directory.Size.Value;
            if (offset <= 0 || size <= 0 || (long)offset + size > Buffer.Length)
            {
                continue;
            }

            string name = directory.Text;
            string text = $"NGEN {name}";
            Node node = name switch
            {
                "ImportSections" => new RecordTable { Count = size / 20, Fields = importSectionFields, RecordText = "Import section" },
                "VersionInfo" when size >= 72 => new Record
                {
                    Fields =
                    [
                        ("OS platform id", 2),
                        ("OS major version", 2),
                        ("Runtime major version", 2),
                        ("Runtime minor version", 2),
                        ("Runtime build number", 2),
                        ("Runtime private build number", 2),
                        ("Codegen flags", 2),
                        ("Config flags", 2),
                        ("Build", 2),
                        ("Machine", 2),
                        ("CPU info", 12),
                        ("Source assembly MVID", 16),
                        ("Source assembly timestamp", 4),
                        ("Source assembly IL image size", 4),
                        ("Native image signature", 16),
                        ("Runtime DLL info", size - 72)
                    ]
                },
                "Dependencies" when size % 48 == 0 => new RecordTable
                {
                    Count = size / 48,
                    Fields =
                    [
                        ("Assembly ref token", 4),
                        ("Assembly def token", 4),
                        ("Assembly MVID", 16),
                        ("Assembly timestamp", 4),
                        ("Assembly IL image size", 4),
                        ("Native image signature", 16)
                    ],
                    RecordText = "Dependency"
                },
                "CodeManagerTable" when size >= 44 => new Record
                {
                    Fields =
                    [
                        ("Hot code RVA", 4),
                        ("Hot code size", 4),
                        ("Code RVA", 4),
                        ("Code size", 4),
                        ("Cold code RVA", 4),
                        ("Cold code size", 4),
                        ("Read-only data RVA", 4),
                        ("Read-only data size", 4),
                        ("Hot IBC method offset", 4),
                        ("Hot generics method offset", 4),
                        ("Cold untrained method offset", 4),
                        ("Remaining", size - 44)
                    ]
                },
                "VirtualSectionsTable" => new RecordTable
                {
                    Count = size / 12,
                    Fields = [("Virtual address", 4), ("Size", 4), ("Section type", 4)],
                    RecordText = "Virtual section"
                },
                _ => null
            };

            if (name == "ManifestMetaData")
            {
                AddSecondaryMetadata(offset, text);
                continue;
            }

            if (node == null)
            {
                TryAddRegion(new Region { Start = offset, Length = size, Text = text });
                continue;
            }

            node.Start = offset;
            node.Length = size;
            if (!TryAdd(node))
            {
                continue;
            }

            node.Text = text;
            if (name == "ImportSections")
            {
                AddReadyToRunImportSections((RecordTable)node);
            }
            else if (name == "VirtualSectionsTable")
            {
                header.VirtualSections = (RecordTable)node;
            }
        }
    }

    private void AddNativeImageVirtualSections()
    {
        var table = NativeImageHeader?.VirtualSections;
        if (table == null)
        {
            return;
        }

        foreach (var record in table.Children.OfType<Record>())
        {
            int rva = record.ReadInt32(0);
            int size = record.ReadInt32(1);
            int type = record.ReadInt32(2);
            int sectionType = type & 0xFFFF;
            string name = sectionType < nativeImageSectionNames.Length ? nativeImageSectionNames[sectionType] : $"type {sectionType}";
            string range = ((type >> 16) & 0xFF) switch
            {
                0x01 => "hot",
                0x02 => "warm",
                0x04 => "cold",
                0x08 => "hot writeable",
                0x10 => "writeable",
                0x20 => "read-only warm",
                0x40 => "read-only cold",
                _ => null
            };
            record.Text = $"Virtual section: {name}{(range == null ? "" : $" ({range})")}, RVA 0x{rva:X}, {size} bytes";

            int offset = rva > 0 ? ResolveVirtualAddress(rva) : 0;
            if (offset <= 0 || size <= 0)
            {
                continue;
            }

            var sectionHeader = GetSectionAtVirtualAddress(rva);
            int rawEnd = sectionHeader.PointerToRawData.Value + sectionHeader.SizeOfRawData.Value;
            int length = Math.Min(size, rawEnd - offset);
            TryAddRegion(new Region
            {
                Start = offset,
                Length = length,
                Text = $"NGEN {name} section{(range == null ? "" : $" ({range})")}",
                ContainsCode = name is "Code" or "VirtualImportThunk" or "ExternalMethodThunk" or "HelperTable"
                    or "MethodPrecode" or "MethodPrecodeWrite" or "MethodPrecodeWriteable" or "Stubs"
            });
        }
    }
}

/// <summary>
/// CORCOMPILE_HEADER of an NGEN native image, pointed to by the managed
/// native header field of the CLI header.
/// </summary>
public class NativeImageHeader : Node
{
    public bool IsPE32Plus { get; set; }

    public override void Parse()
    {
        AddFourBytes("Signature (NGEN)");
        var major = AddTwoBytes("Major version");
        var minor = AddTwoBytes("Minor version");

        string[] names =
        [
            "HelperTable", "ImportSections", "Dummy0", "StubsData", "VersionInfo", "Dependencies",
            "DebugMap", "ModuleImage", "CodeManagerTable", "ProfileDataList", "ManifestMetaData",
            "VirtualSectionsTable"
        ];
        var directories = new List<DataDirectory>();
        foreach (var name in names)
        {
            directories.Add(Add<DataDirectory>(name));
        }

        AddField("Image base", IsPE32Plus ? 8 : 4);
        AddFourBytes("Flags");
        AddFourBytes("PE kind");
        AddFourBytes("COR20 flags");
        AddTwoBytes("Machine");
        AddTwoBytes("Characteristics");
        directories.Add(Add<DataDirectory>("EEInfoTable"));
        AddBytes(Remaining, "Reserved directories");

        Directories = directories;
        Text = $"NGEN native image header {major.Value}.{minor.Value}";
    }

    public IReadOnlyList<DataDirectory> Directories { get; set; }
    public RecordTable VirtualSections { get; set; }
}
