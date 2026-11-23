using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public class LoadConfigDirectory : Node
{
    public LoadConfigDirectory()
    {
        Text = "Load config table";
    }

    public bool IsPE32Plus { get; set; }
    public PEFile PEFile { get; set; }

    private readonly Dictionary<string, Node> fields = new();

    public override void Parse()
    {
        int actualSize = Buffer.ReadInt32(Start);
        if (actualSize < 4 ||
            actualSize > Buffer.Length - Start ||
            Parent is Section section && actualSize > section.End - Start ||
            actualSize > Length &&
                (PEFile == null || !PEFile.IsRangeAvailable(End, actualSize - Length)))
        {
            return;
        }

        Length = actualSize;
        AddFourBytes("Size");

        AddNamedFields(actualSize);

        int remaining = actualSize - (LastChildEnd - Start);
        if (remaining > 0)
        {
            AddBytes(remaining, "Additional load config fields");
        }

        if (PEFile == null)
        {
            return;
        }

        if (!IsPE32Plus)
        {
            AddTable("SE handler table", "SE handler count", 4, "Safe exception handler table", "Handler RVA");
        }

        uint guardFlags = fields.TryGetValue("Guard flags", out var guardFlagsNode) ? Buffer.ReadUInt32(guardFlagsNode.Start) : 0;
        if (guardFlagsNode != null)
        {
            guardFlagsNode.Text = $"Guard flags: {DescribeGuardFlags(guardFlags)}";
        }

        int guardEntrySize = 4 + (int)(guardFlags >> 28);
        AddTable("Guard CF function table", "Guard CF function count", guardEntrySize, "Guard CF function table", "Function RVA");
        AddTable("Guard address taken IAT entry table", "Guard address taken IAT entry count", guardEntrySize, "Guard CF address taken IAT table", "IAT entry RVA");
        AddTable("Guard long jump target table", "Guard long jump target count", guardEntrySize, "Guard CF long jump table", "Target RVA");
        AddTable("Guard EH continuation table", "Guard EH continuation count", guardEntrySize, "Guard EH continuation table", "Continuation RVA");

        AddPointerSlot("Security cookie", "Security cookie");
        AddPointerSlot("Guard CF check function pointer", "Guard CF check function pointer");
        AddPointerSlot("Guard CF dispatch function pointer", "Guard CF dispatch function pointer");
        AddPointerSlot("Guard RF failure routine function pointer", "Guard RF failure routine function pointer");
        AddPointerSlot("Guard RF verify stack pointer function pointer", "Guard RF verify stack pointer function pointer");
        AddPointerSlot("Guard XFG check function pointer", "Guard XFG check function pointer");
        AddPointerSlot("Guard XFG dispatch function pointer", "Guard XFG dispatch function pointer");
        AddPointerSlot("Guard XFG table dispatch function pointer", "Guard XFG table dispatch function pointer");
        AddPointerSlot("CastGuard OS determined failure mode", "CastGuard OS determined failure mode");
        AddPointerSlot("Guard memcpy function pointer", "Guard memcpy function pointer");
        AddPointerSlot("UMA function pointers", "UMA function pointers");

        AddLockPrefixTable();
        AddVolatileMetadata();
        AddEnclaveConfiguration();
        AddChpeMetadata();
        AddHotPatchInfo();
        AddDynamicValueRelocationTable();
    }

    private static string DescribeGuardFlags(uint flags)
    {
        (uint bit, string name)[] known =
        [
            (0x00000100, "CF instrumented"),
            (0x00000200, "CFW instrumented"),
            (0x00000400, "CF function table present"),
            (0x00000800, "Security cookie unused"),
            (0x00001000, "Protect delay-load IAT"),
            (0x00002000, "Delay-load IAT in its own section"),
            (0x00004000, "CF export suppression info present"),
            (0x00008000, "CF enable export suppression"),
            (0x00010000, "CF long jump table present"),
            (0x00020000, "RF instrumented"),
            (0x00040000, "RF enable"),
            (0x00080000, "RF strict"),
            (0x00100000, "Retpoline present"),
            (0x00400000, "EH continuation table present"),
            (0x00800000, "XFG enabled"),
            (0x01000000, "CastGuard present"),
            (0x02000000, "Memcpy present")
        ];
        var names = known.Where(k => (flags & k.bit) != 0).Select(k => k.name).ToList();
        uint entrySize = flags >> 28;
        if (entrySize != 0)
        {
            names.Add($"{entrySize} extra bytes per table entry");
        }

        return names.Count == 0 ? $"0x{flags:X}" : string.Join(", ", names);
    }

    private void AddNamedFields(int actualSize)
    {
        // (name, size) pairs in struct order; 0 stands for pointer-sized
        (string name, int size)[] layout =
        [
            ("TimeDate stamp", 4),
            ("Major version", 2),
            ("Minor version", 2),
            ("Global flags clear", 4),
            ("Global flags set", 4),
            ("Critical section default timeout", 4),
            ("DeCommit free block threshold", 0),
            ("DeCommit total free threshold", 0),
            ("Lock prefix table", 0),
            ("Maximum allocation size", 0),
            ("Virtual memory threshold", 0),
            IsPE32Plus ? ("Process affinity mask", 0) : ("Process heap flags", 4),
            IsPE32Plus ? ("Process heap flags", 4) : ("Process affinity mask", 4),
            ("CSD version", 2),
            ("Dependent load flags", 2),
            ("Edit list", 0),
            ("Security cookie", 0),
            ("SE handler table", 0),
            ("SE handler count", 0),
            ("Guard CF check function pointer", 0),
            ("Guard CF dispatch function pointer", 0),
            ("Guard CF function table", 0),
            ("Guard CF function count", 0),
            ("Guard flags", 4),
            ("Code integrity flags", 2),
            ("Code integrity catalog", 2),
            ("Code integrity catalog offset", 4),
            ("Code integrity reserved", 4),
            ("Guard address taken IAT entry table", 0),
            ("Guard address taken IAT entry count", 0),
            ("Guard long jump target table", 0),
            ("Guard long jump target count", 0),
            ("Dynamic value reloc table", 0),
            ("CHPE metadata pointer", 0),
            ("Guard RF failure routine", 0),
            ("Guard RF failure routine function pointer", 0),
            ("Dynamic value reloc table offset", 4),
            ("Dynamic value reloc table section", 2),
            ("Reserved2", 2),
            ("Guard RF verify stack pointer function pointer", 0),
            ("Hot patch table offset", 4),
            ("Reserved3", 4),
            ("Enclave configuration pointer", 0),
            ("Volatile metadata pointer", 0),
            ("Guard EH continuation table", 0),
            ("Guard EH continuation count", 0),
            ("Guard XFG check function pointer", 0),
            ("Guard XFG dispatch function pointer", 0),
            ("Guard XFG table dispatch function pointer", 0),
            ("CastGuard OS determined failure mode", 0),
            ("Guard memcpy function pointer", 0),
            ("UMA function pointers", 0),
        ];

        int pointerSize = IsPE32Plus ? 8 : 4;
        foreach (var (name, size) in layout)
        {
            int fieldSize = size == 0 ? pointerSize : size;
            if (LastChildEnd - Start + fieldSize > actualSize)
            {
                break;
            }

            fields[name] = AddField(name, fieldSize);
        }
    }

    private ulong ReadField(string name)
    {
        if (!fields.TryGetValue(name, out var node))
        {
            return 0;
        }

        return node.Length == 8 ? Buffer.ReadUInt64(node.Start) : Buffer.ReadUInt32(node.Start);
    }

    /// <summary>
    /// File offset of the data a virtual-address field points to, or 0.
    /// </summary>
    private int ResolveField(string name)
    {
        ulong value = ReadField(name);
        return value == 0 ? 0 : PEFile.ResolvePointer(value);
    }

    private void AddPointerSlot(string fieldName, string text)
    {
        int offset = ResolveField(fieldName);
        if (offset <= 0)
        {
            return;
        }

        int pointerSize = IsPE32Plus ? 8 : 4;
        BytesNode slot = IsPE32Plus ? new EightBytes() : new FourBytes();
        slot.Start = offset;
        if (PEFile.IsRangeAvailable(offset, pointerSize))
        {
            PEFile.Add(slot);
            slot.Text = text;
        }
    }

    private void AddTable(string tableField, string countField, int entrySize, string text, string entryText)
    {
        int offset = ResolveField(tableField);
        ulong count = ReadField(countField);
        if (offset <= 0 || count == 0 || count > (ulong)(int.MaxValue / entrySize))
        {
            return;
        }

        var fieldList = new List<(string, int)> { (entryText, 4) };
        if (entrySize > 4)
        {
            fieldList.Add(("Flags", entrySize - 4));
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = (int)count * entrySize,
            Count = (int)count,
            Fields = fieldList.ToArray(),
            Describe = record => $"0x{record.ReadInt32(0):X}"
        };
        if (PEFile.TryAdd(table))
        {
            table.Text = $"{text} ({count} entries)";
        }
    }

    private void AddLockPrefixTable()
    {
        int offset = ResolveField("Lock prefix table");
        if (offset <= 0)
        {
            return;
        }

        int pointerSize = IsPE32Plus ? 8 : 4;
        int count = 0;
        while ((long)offset + (count + 1) * pointerSize <= Buffer.Length && count < 0x10000)
        {
            count++;
            if (PEFile.ReadPointer(offset + (count - 1) * pointerSize) == 0)
            {
                break;
            }
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = count * pointerSize,
            Count = count,
            Fields = [("Lock prefix address", pointerSize)]
        };
        if (PEFile.TryAdd(table))
        {
            table.Text = $"Lock prefix table ({count - 1} entries)";
        }
    }

    private void AddVolatileMetadata()
    {
        int offset = ResolveField("Volatile metadata pointer");
        if (offset <= 0 || offset + 24 > Buffer.Length || Buffer.ReadInt32(offset) < 24)
        {
            return;
        }

        var metadata = new Record
        {
            Start = offset,
            Length = 24,
            Fields =
            [
                ("Size", 4),
                ("Version", 4),
                ("Volatile access table RVA", 4),
                ("Volatile access table size", 4),
                ("Volatile info range table RVA", 4),
                ("Volatile info range table size", 4)
            ]
        };
        if (!PEFile.TryAdd(metadata))
        {
            return;
        }

        metadata.Text = "Volatile metadata";
        AddRvaTable(metadata.ReadInt32(2), metadata.ReadInt32(3), "Volatile access table", [("Access RVA", 4)]);
        AddRvaTable(metadata.ReadInt32(4), metadata.ReadInt32(5), "Volatile info range table", [("Range RVA", 4), ("Range size", 4)]);
    }

    private RecordTable AddRvaTable(int rva, int size, string text, (string, int)[] recordFields, bool sizeIsCount = false)
    {
        int recordSize = recordFields.Sum(f => f.Item2);
        int offset = rva > 0 ? PEFile.ResolveVirtualAddress(rva) : 0;
        long byteSize = sizeIsCount ? (long)size * recordSize : size;
        if (offset <= 0 || byteSize < recordSize || offset + byteSize > Buffer.Length)
        {
            return null;
        }

        int count = (int)(byteSize / recordSize);
        var table = new RecordTable
        {
            Start = offset,
            Length = count * recordSize,
            Count = count,
            Fields = recordFields,
            Describe = record => $"0x{record.ReadInt32(0):X}"
        };
        if (!PEFile.TryAdd(table))
        {
            return null;
        }

        table.Text = $"{text} ({count} entries)";
        return table;
    }

    private void AddEnclaveConfiguration()
    {
        int offset = ResolveField("Enclave configuration pointer");
        int expectedSize = IsPE32Plus ? 80 : 76;
        if (offset <= 0 || offset + expectedSize > Buffer.Length || Buffer.ReadInt32(offset) < expectedSize - 8)
        {
            return;
        }

        var configuration = new Record
        {
            Start = offset,
            Length = expectedSize,
            Fields =
            [
                ("Size", 4),
                ("Minimum required config size", 4),
                ("Policy flags", 4),
                ("Number of imports", 4),
                ("Import list RVA", 4),
                ("Import entry size", 4),
                ("Family ID", 16),
                ("Image ID", 16),
                ("Image version", 4),
                ("Security version", 4),
                ("Enclave size", IsPE32Plus ? 8 : 4),
                ("Number of threads", 4),
                ("Enclave flags", 4)
            ]
        };
        if (!PEFile.TryAdd(configuration))
        {
            return;
        }

        configuration.Text = "Enclave configuration";
        int importCount = configuration.ReadInt32(3);
        int importListRva = configuration.ReadInt32(4);
        int importEntrySize = configuration.ReadInt32(5);
        if (importCount <= 0 || importCount > 4096 || importEntrySize < 80)
        {
            return;
        }

        var imports = AddRvaTable(
            importListRva,
            importCount,
            "Enclave imports",
            [
                ("Match type", 4),
                ("Minimum security version", 4),
                ("Unique or author ID", 32),
                ("Family ID", 16),
                ("Image ID", 16),
                ("Import name RVA", 4),
                ("Reserved", importEntrySize - 76)
            ],
            sizeIsCount: true);
        if (imports == null)
        {
            return;
        }

        foreach (var import in imports.Children.OfType<Record>())
        {
            int nameRva = import.ReadInt32(5);
            int nameOffset = nameRva > 0 ? PEFile.ResolveVirtualAddress(nameRva) : 0;
            string name = PEFile.ReadZeroTerminatedAscii(nameOffset);
            if (name != null)
            {
                import.Text = $"Enclave import: {name}";
                if (PEFile.IsRangeAvailable(nameOffset, name.Length + 1))
                {
                    PEFile.Add(new ZeroTerminatedString { Start = nameOffset });
                }
            }
        }
    }

    private void AddChpeMetadata()
    {
        int offset = ResolveField("CHPE metadata pointer");
        if (offset <= 0 || offset + 8 > Buffer.Length)
        {
            return;
        }

        int version = Buffer.ReadInt32(offset);
        if (version < 1 || version > 16)
        {
            return;
        }

        if (PEFile.Machine == PEFile.MachineI386)
        {
            var names = new List<(string, int)>
            {
                ("Version", 4),
                ("CHPE code address range offset", 4),
                ("CHPE code address range count", 4),
                ("WowA64 exception handler function pointer", 4),
                ("WowA64 dispatch call function pointer", 4),
                ("WowA64 dispatch indirect call function pointer", 4),
                ("WowA64 dispatch indirect call CFG function pointer", 4),
                ("WowA64 dispatch return function pointer", 4),
                ("WowA64 dispatch return leaf function pointer", 4),
                ("WowA64 dispatch jump function pointer", 4)
            };
            if (version >= 2)
            {
                names.Add(("Compiler IAT pointer", 4));
            }

            if (version >= 3)
            {
                names.Add(("WowA64 RDTSC function pointer", 4));
            }

            var metadata = new Record { Start = offset, Length = names.Count * 4, Fields = names.ToArray() };
            if (!PEFile.TryAdd(metadata))
            {
                return;
            }

            metadata.Text = $"CHPE metadata (x86, version {version})";
            AddRvaTable(
                metadata.ReadInt32(1),
                metadata.ReadInt32(2),
                "CHPE code address ranges",
                [("Start RVA and type", 4), ("Length", 4)],
                sizeIsCount: true);
            return;
        }

        var arm64ecFields = new List<(string, int)>
        {
            ("Version", 4),
            ("Code map RVA", 4),
            ("Code map count", 4),
            ("Code ranges to entry points RVA", 4),
            ("Redirection metadata RVA", 4),
            ("__os_arm64x_dispatch_call_no_redirect", 4),
            ("__os_arm64x_dispatch_ret", 4),
            ("__os_arm64x_dispatch_call", 4),
            ("__os_arm64x_dispatch_icall", 4),
            ("__os_arm64x_dispatch_icall_cfg", 4),
            ("Alternate entry point", 4),
            ("Auxiliary IAT RVA", 4),
            ("Code ranges to entry points count", 4),
            ("Redirection metadata count", 4),
            ("GetX64Information function pointer", 4),
            ("SetX64Information function pointer", 4),
            ("Extra RFE table RVA", 4),
            ("Extra RFE table size", 4),
            ("__os_arm64x_dispatch_fptr", 4),
            ("Auxiliary IAT copy RVA", 4)
        };
        if (version >= 2)
        {
            arm64ecFields.Add(("Auxiliary delay-load IAT RVA", 4));
            arm64ecFields.Add(("Auxiliary delay-load IAT copy RVA", 4));
            arm64ecFields.Add(("Hybrid image info bitfield", 4));
        }

        var arm64ec = new Record { Start = offset, Length = arm64ecFields.Count * 4, Fields = arm64ecFields.ToArray() };
        if (!PEFile.TryAdd(arm64ec))
        {
            return;
        }

        arm64ec.Text = $"CHPE metadata (ARM64EC, version {version})";
        var codeMap = AddRvaTable(
            arm64ec.ReadInt32(1),
            arm64ec.ReadInt32(2),
            "ARM64EC code map",
            [("Start RVA and type", 4), ("Length", 4)],
            sizeIsCount: true);
        if (codeMap != null)
        {
            foreach (var range in codeMap.Children.OfType<Record>())
            {
                int start = range.ReadInt32(0);
                string kind = (start & 3) switch
                {
                    0 => "ARM64",
                    1 => "ARM64EC",
                    2 => "x64",
                    _ => "reserved"
                };
                range.Text = $"{kind} code at 0x{start & ~3:X}, {range.ReadInt32(1)} bytes";
            }
        }

        AddRvaTable(
            arm64ec.ReadInt32(3),
            arm64ec.ReadInt32(12),
            "ARM64EC code ranges to entry points",
            [("Start RVA", 4), ("End RVA", 4), ("Entry point RVA", 4)],
            sizeIsCount: true);
        AddRvaTable(
            arm64ec.ReadInt32(4),
            arm64ec.ReadInt32(13),
            "ARM64EC redirection metadata",
            [("Source RVA", 4), ("Destination RVA", 4)],
            sizeIsCount: true);

        int extraTableRva = arm64ec.ReadInt32(16);
        int extraTableSize = arm64ec.ReadInt32(17);
        int extraTableOffset = extraTableRva > 0 ? PEFile.ResolveVirtualAddress(extraTableRva) : 0;
        if (extraTableOffset > 0 && extraTableSize >= 12)
        {
            var extraTable = new ExceptionTable
            {
                Start = extraTableOffset,
                Length = extraTableSize,
                MachineType = ExceptionTable.MachineAmd64,
                IsPE32Plus = true,
                PEFile = PEFile
            };
            if (PEFile.TryAdd(extraTable))
            {
                extraTable.Text = "ARM64EC extra function table (x64 code)";
            }
        }
    }

    private void AddHotPatchInfo()
    {
        int rva = (int)ReadField("Hot patch table offset");
        int offset = rva > 0 ? PEFile.ResolveVirtualAddress(rva) : 0;
        if (offset <= 0 || offset + 20 > Buffer.Length)
        {
            return;
        }

        int version = Buffer.ReadInt32(offset);
        int size = Buffer.ReadInt32(offset + 4);
        if (version < 1 || version > 8 || size < 20)
        {
            return;
        }

        var infoFields = new List<(string, int)>
        {
            ("Version", 4),
            ("Size", 4),
            ("Sequence number", 4),
            ("Base image list", 4),
            ("Base image count", 4)
        };
        if (version >= 2)
        {
            infoFields.Add(("Buffer offset", 4));
        }

        if (version >= 3)
        {
            infoFields.Add(("Extra patch size", 4));
        }

        var info = new Record { Start = offset, Length = infoFields.Count * 4, Fields = infoFields.ToArray() };
        if (PEFile.TryAdd(info))
        {
            info.Text = "Hot patch information";
        }
    }

    private void AddDynamicValueRelocationTable()
    {
        // The DVRT is located by a raw offset into the section given by a
        // 1-based section index (usually .reloc), not by RVA
        int tableOffset = (int)ReadField("Dynamic value reloc table offset");
        int sectionIndex = (int)ReadField("Dynamic value reloc table section");
        var sections = PEFile.SectionTable.SectionHeaders;
        if (tableOffset <= 0 || sectionIndex <= 0 || sectionIndex > sections.Count)
        {
            return;
        }

        var section = sections[sectionIndex - 1];
        long fileOffset = (long)section.PointerToRawData.Value + tableOffset;
        if (fileOffset <= 0 || fileOffset + 8 > Buffer.Length)
        {
            return;
        }

        int version = Buffer.ReadInt32((int)fileOffset);
        int size = Buffer.ReadInt32((int)fileOffset + 4);
        if (version < 1 || version > 2 || size <= 0 || fileOffset + 8 + size > Buffer.Length)
        {
            return;
        }

        PEFile.TryAdd(new DynamicValueRelocationTable
        {
            Start = (int)fileOffset,
            Length = 8 + size,
            IsPE32Plus = IsPE32Plus
        });
    }
}

public class DynamicValueRelocationTable : Node
{
    public DynamicValueRelocationTable()
    {
        Text = "Dynamic value relocation table";
    }

    public bool IsPE32Plus { get; set; }

    public override void Parse()
    {
        Version = AddFourBytes("Version");
        Size = AddFourBytes("Size");

        if (Version.Value != 1)
        {
            AddBytes(End - LastChildEnd, $"Dynamic relocations (version {Version.Value})");
            return;
        }

        int symbolSize = IsPE32Plus ? 8 : 4;
        while (LastChildEnd + symbolSize + 4 <= End)
        {
            long symbol = IsPE32Plus
                ? (long)Buffer.ReadUInt64(LastChildEnd)
                : Buffer.ReadUInt32(LastChildEnd);
            int baseRelocSize = Buffer.ReadInt32(LastChildEnd + symbolSize);
            if (baseRelocSize < 0 || LastChildEnd + symbolSize + 4 + baseRelocSize > End)
            {
                break;
            }

            var entry = new DynamicRelocationEntry
            {
                IsPE32Plus = IsPE32Plus,
                Length = symbolSize + 4 + baseRelocSize,
                Symbol = symbol
            };
            Add(entry);
        }

        int remaining = End - LastChildEnd;
        if (remaining > 0)
        {
            AddBytes(remaining, "Remaining bytes");
        }
    }

    public FourBytes Version { get; set; }
    public FourBytes Size { get; set; }
}

public class DynamicRelocationEntry : Node
{
    public bool IsPE32Plus { get; set; }
    public long Symbol { get; set; }

    public const int PrologueSymbol = 1;
    public const int EpilogueSymbol = 2;
    public const int ImportControlTransferSymbol = 3;
    public const int IndirectControlTransferSymbol = 4;
    public const int SwitchTableBranchSymbol = 5;
    public const int Arm64XSymbol = 6;
    public const int FunctionOverrideSymbol = 7;
    public const int Arm64KernelImportCallTransferSymbol = 8;

    public override void Parse()
    {
        var symbolNode = AddFourOrEightBytes(IsPE32Plus);
        symbolNode.Text = "Symbol";
        BaseRelocSize = AddFourBytes("Base reloc size");
        Text = $"Dynamic relocations: {GetSymbolName(Symbol)}";

        int dataLength = BaseRelocSize.Value;
        if (dataLength <= 0)
        {
            return;
        }

        if (Symbol == PrologueSymbol || Symbol == EpilogueSymbol || Symbol == FunctionOverrideSymbol)
        {
            AddHeaderedData(dataLength);
            return;
        }

        int dataEnd = LastChildEnd + dataLength;
        while (LastChildEnd + 8 <= dataEnd)
        {
            int blockSize = Buffer.ReadInt32(LastChildEnd + 4);
            if (blockSize < 8 || LastChildEnd + blockSize > dataEnd)
            {
                break;
            }

            Add(new DynamicRelocationBlock { Length = blockSize, Symbol = Symbol });
        }

        if (dataEnd > LastChildEnd)
        {
            AddBytes(dataEnd - LastChildEnd, "Remaining relocation data");
        }
    }

    private void AddHeaderedData(int dataLength)
    {
        int dataEnd = LastChildEnd + dataLength;
        if (Symbol == PrologueSymbol && dataLength >= 1)
        {
            int byteCount = Buffer.ReadByte(LastChildEnd);
            if (1 + byteCount <= dataLength)
            {
                AddOneByte("Prologue byte count");
                if (byteCount > 0)
                {
                    AddBytes(byteCount, "Prologue bytes");
                }
            }
        }
        else if (Symbol == EpilogueSymbol && dataLength >= 8)
        {
            AddFourBytes("Epilogue count");
            AddOneByte("Epilogue byte count");
            AddOneByte("Branch descriptor element size");
            AddTwoBytes("Branch descriptor count");
        }
        else if (Symbol == FunctionOverrideSymbol && dataLength >= 4)
        {
            AddFourBytes("Function override size");
        }

        if (dataEnd > LastChildEnd)
        {
            AddBytes(dataEnd - LastChildEnd, "Relocation data");
        }
    }

    public static string GetSymbolName(long symbol)
    {
        return symbol switch
        {
            PrologueSymbol => "Guard RF prologue",
            EpilogueSymbol => "Guard RF epilogue",
            ImportControlTransferSymbol => "Guard import control transfer (Retpoline)",
            IndirectControlTransferSymbol => "Guard indirect control transfer (Retpoline)",
            SwitchTableBranchSymbol => "Guard switchtable branch (Retpoline)",
            Arm64XSymbol => "ARM64X",
            FunctionOverrideSymbol => "Function override",
            Arm64KernelImportCallTransferSymbol => "ARM64 kernel import call transfer",
            _ => $"Symbol 0x{symbol:X}"
        };
    }

    public FourBytes BaseRelocSize { get; set; }
}

/// <summary>
/// One page of dynamic relocations. The entry layout depends on the symbol
/// of the enclosing entry.
/// </summary>
public class DynamicRelocationBlock : Node
{
    public long Symbol { get; set; }

    public override void Parse()
    {
        var pageRva = AddFourBytes("Page RVA");
        AddFourBytes("Block size");
        int page = pageRva.Value;
        Text = $"Page 0x{page:X}";

        if (Symbol == DynamicRelocationEntry.Arm64XSymbol)
        {
            AddArm64XEntries(page);
        }
        else
        {
            int entrySize = Symbol is DynamicRelocationEntry.ImportControlTransferSymbol
                or DynamicRelocationEntry.Arm64KernelImportCallTransferSymbol ? 4 : 2;
            while (Remaining >= entrySize)
            {
                if (entrySize == 4)
                {
                    uint value = Buffer.ReadUInt32(LastChildEnd);
                    string text = Symbol == DynamicRelocationEntry.ImportControlTransferSymbol
                        ? $"0x{page + (value & 0xFFF):X} {((value & 0x1000) != 0 ? "call" : "jmp")} IAT index {value >> 13}"
                        : $"0x{page + (value & 0xFFF):X}";
                    AddFourBytes(text);
                }
                else
                {
                    int value = Buffer.ReadUInt16(LastChildEnd);
                    int address = page + (value & 0xFFF);
                    string text = Symbol switch
                    {
                        DynamicRelocationEntry.IndirectControlTransferSymbol =>
                            $"0x{address:X} {((value & 0x1000) != 0 ? "call" : "jmp")}{((value & 0x2000) != 0 ? ", REX.W" : "")}{((value & 0x4000) != 0 ? ", CFG check" : "")}",
                        DynamicRelocationEntry.SwitchTableBranchSymbol => $"0x{address:X} register {value >> 12}",
                        _ => value == 0 ? "Padding entry" : $"0x{address:X} type {value >> 12}"
                    };
                    AddTwoBytes(text);
                }
            }
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Padding");
        }
    }

    private void AddArm64XEntries(int page)
    {
        while (Remaining >= 2)
        {
            int value = Buffer.ReadUInt16(LastChildEnd);
            if (value == 0)
            {
                break;
            }

            int address = page + (value & 0xFFF);
            int type = (value >> 12) & 3;
            int meta = value >> 14;
            int payload;
            string text;
            switch (type)
            {
                case 0:
                    payload = 0;
                    text = $"0x{address:X} zero fill {1 << meta} bytes";
                    break;
                case 1:
                    payload = 1 << meta;
                    text = $"0x{address:X} assign {payload} bytes";
                    break;
                case 2:
                    payload = 2;
                    text = $"0x{address:X} add delta (scale {((meta & 2) != 0 ? 8 : 4)}, {((meta & 1) != 0 ? "negative" : "positive")})";
                    break;
                default:
                    return;
            }

            if (2 + payload > Remaining)
            {
                return;
            }

            AddBytes(2 + payload, text);
        }
    }
}
