namespace GuiLabs.FileFormat.PE;

using System.Collections.Generic;

public class ExceptionTable : Node
{
    public ExceptionTable()
    {
        Text = "Exception table";
    }

    public const int MachineI386 = 0x014C;
    public const int MachineAmd64 = 0x8664;
    public const int MachineIA64 = 0x200;
    public const int MachineArm = 0x01C0;
    public const int MachineThumb = 0x01C2;
    public const int MachineArmNT = 0x01C4;
    public const int MachineArm64 = 0xAA64;
    public const int MachineArm64EC = 0xA641;
    public const int MachineArm64X = 0xA64E;

    public bool IsPE32Plus { get; set; }
    public int MachineType { get; set; }
    public PEFile PEFile { get; set; }

    /// <summary>
    /// True for the function table of ReadyToRun and NGEN code. There the
    /// unwind info is followed by GC info instead of handler data, and on
    /// x86 the table uses the 8-byte entry layout.
    /// </summary>
    public bool IsManaged { get; set; }

    // The machine value arrives sign-extended from a short field
    public int Machine => MachineType & 0xFFFF;

    public bool IsAmd64 => Machine == MachineAmd64;
    public bool IsArm64 => Machine == MachineArm64 || Machine == MachineArm64EC || Machine == MachineArm64X;
    public bool IsArm32 => Machine == MachineArm || Machine == MachineThumb || Machine == MachineArmNT;

    // MIPS, Alpha and PowerPC images use the five-field IMAGE_FUNCTION_ENTRY-style record
    public bool IsLegacyRisc => Machine is 0x0162 or 0x0166 or 0x0168 or 0x0169 or 0x0266 or 0x0366 or 0x0466
        or 0x0184 or 0x0284 or 0x01F0 or 0x01F1;

    /// <summary>
    /// Unwind infos that name an exception handler, with the offset of the
    /// handler data that follows each.
    /// </summary>
    public List<HandlerUse> HandlerUses { get; } = new();

    /// <summary>
    /// x64 unwind infos that have neither a handler nor a chained entry.
    /// </summary>
    public List<Node> PlainUnwindInfos { get; } = new();

    /// <summary>
    /// File offsets of the unwind infos this table references.
    /// </summary>
    public HashSet<int> UnwindOffsets { get; } = new();

    public override void Parse()
    {
        int entrySize;
        if (IsAmd64 || Machine == MachineIA64)
        {
            entrySize = 12;
        }
        else if (IsArm64 || IsArm32 || (IsManaged && Machine == MachineI386))
        {
            entrySize = 8;
        }
        else if (IsLegacyRisc)
        {
            entrySize = 20;
        }
        else
        {
            return;
        }

        int count = Length / entrySize;

        for (int i = 0; i < count; i++)
        {
            if (entrySize == 12)
            {
                var entry = Add<RuntimeFunction>("RUNTIME_FUNCTION");
                entry.Describe(PEFile);
                if (PEFile != null)
                {
                    AddNativeCodeRange(entry.BeginAddress.Value, entry.EndAddress.Value);
                }
            }
            else if (entrySize == 8)
            {
                var entry = Add<ArmRuntimeFunction>("RUNTIME_FUNCTION");
                entry.Describe(this);
            }
            else
            {
                var entry = new Record
                {
                    Fields =
                    [
                        ("Begin address", 4),
                        ("End address", 4),
                        ("Exception handler", 4),
                        ("Handler data", 4),
                        ("Prolog end address", 4)
                    ]
                };
                Add(entry);
                uint begin = Buffer.ReadUInt32(entry.Start);
                uint end = Buffer.ReadUInt32(entry.Start + 4);
                entry.Text = $"Function entry 0x{begin:X}-0x{end:X}";
                if (PEFile != null)
                {
                    int beginRva = PEFile.VirtualAddressToRva(begin);
                    int endRva = PEFile.VirtualAddressToRva(end);
                    if (beginRva > 0 && endRva > beginRva)
                    {
                        AddNativeCodeRange(beginRva, endRva);
                    }
                }
            }
        }

        int trailingBytes = Length - count * entrySize;
        if (trailingBytes > 0)
        {
            AddBytes(trailingBytes, "Trailing bytes");
        }

        if (PEFile == null)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            if (Children[i] is RuntimeFunction function && IsAmd64)
            {
                AddAmd64UnwindInfo(function.BeginAddress.Value, function.EndAddress.Value, function.UnwindInfoAddress.Value, depth: 0);
            }
            else if (Children[i] is ArmRuntimeFunction armFunction)
            {
                AddCompactUnwindInfo(armFunction);
            }
        }
    }

    private void AddNativeCodeRange(int beginRva, int endRva)
    {
        if (PEFile == null || endRva <= beginRva)
        {
            return;
        }

        // Both RVAs must resolve to the same section
        var beginSection = PEFile.GetSectionAtVirtualAddress(beginRva);
        var endSection = PEFile.GetSectionAtVirtualAddress(endRva - 1);
        if (beginSection == null || endSection == null || beginSection != endSection)
        {
            return;
        }

        int beginOffset = PEFile.ResolveVirtualAddressInSection(beginRva, beginSection);
        int endOffset = PEFile.ResolveVirtualAddressInSection(endRva, endSection);
        int rawEnd = beginSection.PointerToRawData.Value + beginSection.SizeOfRawData.Value;
        if (beginOffset <= 0 || endOffset <= beginOffset || endOffset > rawEnd)
        {
            return;
        }

        PEFile.TryAdd(new NativeCode
        {
            Start = beginOffset,
            Length = endOffset - beginOffset,
            Text = $"Native code (0x{beginRva:X}-0x{endRva:X})"
        });
    }

    private void AddAmd64UnwindInfo(int beginRva, int endRva, int unwindRva, int depth)
    {
        // A set low bit redirects to another RUNTIME_FUNCTION instead of an UNWIND_INFO
        if ((unwindRva & 1) != 0 || depth > 8)
        {
            return;
        }

        int offset = PEFile.ResolveVirtualAddress(unwindRva);
        if (offset <= 0 || !UnwindOffsets.Add(offset))
        {
            return;
        }

        int size = Amd64UnwindInfo.Measure(Buffer, offset);
        if (size == 0)
        {
            return;
        }

        var unwindInfo = new Amd64UnwindInfo
        {
            Start = offset,
            Length = size,
            Rva = unwindRva
        };
        if (!PEFile.TryAdd(unwindInfo))
        {
            return;
        }

        if (IsManaged)
        {
            PEFile.ManagedUnwindInfos.Add(unwindInfo);
        }
        else if (unwindInfo.HandlerRva != 0)
        {
            HandlerUses.Add(new HandlerUse
            {
                HandlerRva = unwindInfo.HandlerRva,
                DataOffset = unwindInfo.End,
                FunctionBegin = beginRva,
                FunctionEnd = endRva
            });
        }
        else if (unwindInfo.ChainedFunction == null)
        {
            PlainUnwindInfos.Add(unwindInfo);
        }

        if (unwindInfo.ChainedFunction != null)
        {
            var chained = unwindInfo.ChainedFunction;
            AddAmd64UnwindInfo(chained.BeginAddress.Value, chained.EndAddress.Value, chained.UnwindInfoAddress.Value, depth + 1);
        }
    }

    private void AddCompactUnwindInfo(ArmRuntimeFunction function)
    {
        int unwindData = function.UnwindData.Value;
        int beginRva = function.BeginAddress.Value;

        if (IsManaged && Machine == MachineI386)
        {
            AddX86ManagedUnwindInfo(beginRva, unwindData);
            return;
        }

        // On ARM the low bit of the address marks Thumb code
        if (IsArm32)
        {
            beginRva &= ~1;
        }

        // Bits 1:0 != 0 means the unwind data is packed into the field itself
        if ((unwindData & 3) != 0)
        {
            int packedLength = IsArm64
                ? ((unwindData >> 2) & 0x7FF) * 4
                : ((unwindData >> 2) & 0x7FF) * 2;
            AddNativeCodeRange(beginRva, beginRva + packedLength);
            return;
        }

        int offset = PEFile.ResolveVirtualAddress(unwindData);
        if (offset <= 0 || offset + 4 > Buffer.Length)
        {
            return;
        }

        uint header = Buffer.ReadUInt32(offset);
        int functionLength = (int)(header & 0x3FFFF) * (IsArm64 ? 4 : 2);
        AddNativeCodeRange(beginRva, beginRva + functionLength);

        if (!UnwindOffsets.Add(offset))
        {
            return;
        }

        int size = ArmUnwindInfo.Measure(Buffer, offset, IsArm64);
        if (size == 0)
        {
            return;
        }

        var unwindInfo = new ArmUnwindInfo
        {
            Start = offset,
            Length = size,
            IsArm64 = IsArm64,
            Rva = unwindData
        };
        if (!PEFile.TryAdd(unwindInfo))
        {
            return;
        }

        if (IsManaged)
        {
            PEFile.ManagedUnwindInfos.Add(unwindInfo);
        }
        else if (unwindInfo.HandlerRva != 0)
        {
            HandlerUses.Add(new HandlerUse
            {
                HandlerRva = unwindInfo.HandlerRva,
                DataOffset = unwindInfo.End,
                FunctionBegin = beginRva,
                FunctionEnd = beginRva + functionLength
            });
        }
    }

    private void AddX86ManagedUnwindInfo(int beginRva, int unwindRva)
    {
        // x86 has no unwind codes: the unwind info is the function length
        int offset = PEFile.ResolveVirtualAddress(unwindRva);
        if (offset <= 0 || offset + 4 > Buffer.Length)
        {
            return;
        }

        int functionLength = Buffer.ReadInt32(offset);
        if (functionLength > 0 && functionLength < 0x1000000)
        {
            AddNativeCodeRange(beginRva, beginRva + functionLength);
        }

        if (!UnwindOffsets.Add(offset))
        {
            return;
        }

        var unwindInfo = new Record
        {
            Start = offset,
            Length = 4,
            Fields = [("Function length", 4)],
            Text = $"Unwind info (0x{unwindRva:X})"
        };
        if (PEFile.TryAdd(unwindInfo))
        {
            unwindInfo.Text = $"Unwind info (0x{unwindRva:X})";
            PEFile.ManagedUnwindInfos.Add(unwindInfo);
        }
    }
}

public class HandlerUse
{
    public int HandlerRva { get; set; }
    public int DataOffset { get; set; }
    public int FunctionBegin { get; set; }
    public int FunctionEnd { get; set; }
}

public class RuntimeFunction : Node
{
    public override void Parse()
    {
        BeginAddress = AddFourBytes("BeginAddress");
        EndAddress = AddFourBytes("EndAddress");
        UnwindInfoAddress = AddFourBytes("UnwindInfoAddress");
    }

    public void Describe(PEFile peFile)
    {
        int beginRva = BeginAddress.Value;
        int endRva = EndAddress.Value;
        if (endRva > beginRva)
        {
            Text = $"RUNTIME_FUNCTION 0x{beginRva:X}-0x{endRva:X} ({endRva - beginRva} bytes)";
        }
    }

    public FourBytes BeginAddress { get; set; }
    public FourBytes EndAddress { get; set; }
    public FourBytes UnwindInfoAddress { get; set; }
}

public class ArmRuntimeFunction : Node
{
    public override void Parse()
    {
        BeginAddress = AddFourBytes("BeginAddress");
        UnwindData = AddFourBytes("UnwindData");
    }

    public void Describe(ExceptionTable table)
    {
        int unwindData = UnwindData.Value;
        if (table.IsManaged && table.Machine == ExceptionTable.MachineI386)
        {
            Text = $"RUNTIME_FUNCTION 0x{BeginAddress.Value:X} (unwind info 0x{unwindData:X})";
        }
        else if ((unwindData & 3) != 0)
        {
            Text = $"RUNTIME_FUNCTION 0x{BeginAddress.Value:X} (packed unwind)";
        }
        else
        {
            Text = $"RUNTIME_FUNCTION 0x{BeginAddress.Value:X} (xdata 0x{unwindData:X})";
        }
    }

    public FourBytes BeginAddress { get; set; }
    public FourBytes UnwindData { get; set; }
}

/// <summary>
/// UNWIND_INFO of an x64 function: prolog description, optional handler RVA
/// or chained function entry.
/// </summary>
public class Amd64UnwindInfo : Node
{
    public const int FlagExceptionHandler = 1;
    public const int FlagUnwindHandler = 2;
    public const int FlagChainInfo = 4;

    public int Rva { get; set; }
    public int HandlerRva { get; set; }
    public RuntimeFunction ChainedFunction { get; set; }

    public static int Measure(ByteBuffer buffer, int offset)
    {
        if (offset + 4 > buffer.Length)
        {
            return 0;
        }

        byte versionFlags = buffer.ReadByte(offset);
        int version = versionFlags & 7;
        int flags = versionFlags >> 3;
        if (version < 1 || version > 2)
        {
            return 0;
        }

        int countOfCodes = buffer.ReadByte(offset + 2);
        int size = 4 + ((countOfCodes + 1) & ~1) * 2;
        if ((flags & FlagChainInfo) != 0)
        {
            size += 12;
        }
        else if ((flags & (FlagExceptionHandler | FlagUnwindHandler)) != 0)
        {
            size += 4;
        }

        return offset + size > buffer.Length ? 0 : size;
    }

    private static readonly string[] registers =
    [
        "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
        "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"
    ];

    public override void Parse()
    {
        var versionAndFlags = AddOneByte("Version and flags");
        int version = versionAndFlags.Value & 7;
        int flags = versionAndFlags.Value >> 3;
        var flagNames = new List<string>();
        if ((flags & FlagExceptionHandler) != 0)
        {
            flagNames.Add("EHANDLER");
        }

        if ((flags & FlagUnwindHandler) != 0)
        {
            flagNames.Add("UHANDLER");
        }

        if ((flags & FlagChainInfo) != 0)
        {
            flagNames.Add("CHAININFO");
        }

        versionAndFlags.Text = $"Version {version}, flags {(flagNames.Count == 0 ? "none" : string.Join("|", flagNames))}";
        AddOneByte("Size of prolog");
        var countOfCodes = AddOneByte("Count of unwind codes");
        var frame = AddOneByte("Frame register and offset");
        if (frame.Value != 0)
        {
            frame.Text = $"Frame register {registers[frame.Value & 0xF]}, offset {(frame.Value >> 4) * 16}";
        }

        int count = countOfCodes.Value;
        if (count > 0)
        {
            var codes = new Node { Start = LastChildEnd, Length = count * 2, Text = $"Unwind codes ({count} slots)" };
            Add(codes);
            int slot = 0;
            while (slot < count)
            {
                int codeOffset = codes.Start + slot * 2;
                byte prologOffset = Buffer.ReadByte(codeOffset);
                byte opAndInfo = Buffer.ReadByte(codeOffset + 1);
                int op = opAndInfo & 0xF;
                int info = opAndInfo >> 4;
                int slots = SlotCount(op, info, version);
                if (slot + slots > count)
                {
                    slots = count - slot;
                }

                string text = DescribeCode(op, info, version, codeOffset, slots);
                codes.Add(new Node { Start = codeOffset, Length = slots * 2, Text = $"+0x{prologOffset:X2} {text}" });
                slot += slots;
            }

            if ((count & 1) != 0)
            {
                AddBytes(2, "Alignment slot");
            }
        }

        if ((flags & FlagChainInfo) != 0)
        {
            ChainedFunction = Add<RuntimeFunction>("Chained RUNTIME_FUNCTION");
        }
        else if ((flags & (FlagExceptionHandler | FlagUnwindHandler)) != 0)
        {
            var handler = AddFourBytes("Exception handler RVA");
            HandlerRva = handler.Value;
            handler.Text = $"Exception handler RVA 0x{HandlerRva:X}";
        }

        Text = $"Unwind info (0x{Rva:X})";
    }

    private static int SlotCount(int op, int info, int version)
    {
        return op switch
        {
            1 => info == 0 ? 2 : 3,
            4 or 8 => 2,
            5 or 9 => 3,
            6 => version >= 2 ? 1 : 2,
            7 => version >= 2 ? 2 : 3,
            _ => 1
        };
    }

    private string DescribeCode(int op, int info, int version, int offset, int slots)
    {
        int next16 = slots >= 2 ? Buffer.ReadUInt16(offset + 2) : 0;
        uint next32 = slots >= 3 ? Buffer.ReadUInt32(offset + 2) : 0;
        return op switch
        {
            0 => $"PUSH_NONVOL {registers[info]}",
            1 => info == 0 ? $"ALLOC_LARGE {next16 * 8}" : $"ALLOC_LARGE {next32}",
            2 => $"ALLOC_SMALL {info * 8 + 8}",
            3 => "SET_FPREG",
            4 => $"SAVE_NONVOL {registers[info]} at {next16 * 8}",
            5 => $"SAVE_NONVOL_FAR {registers[info]} at {next32}",
            6 => version >= 2 ? $"EPILOG {info}" : $"SAVE_XMM xmm{info} at {next16 * 8}",
            7 => version >= 2 ? "SPARE_CODE" : $"SAVE_XMM_FAR xmm{info} at {next32}",
            8 => $"SAVE_XMM128 xmm{info} at {next16 * 16}",
            9 => $"SAVE_XMM128_FAR xmm{info} at {next32}",
            10 => info == 0 ? "PUSH_MACHFRAME" : "PUSH_MACHFRAME with error code",
            _ => $"Unwind operation {op} info {info}"
        };
    }
}

/// <summary>
/// An .xdata record of an ARM64 or ARM function: header, epilog scopes,
/// unwind codes and optional handler RVA.
/// </summary>
public class ArmUnwindInfo : Node
{
    public bool IsArm64 { get; set; }
    public int Rva { get; set; }
    public int HandlerRva { get; set; }

    private static void ReadHeader(ByteBuffer buffer, int offset, bool isArm64, out bool hasHandler, out bool epilogInHeader, out int epilogCount, out int codeWords, out bool hasExtension)
    {
        uint header = buffer.ReadUInt32(offset);
        hasHandler = ((header >> 20) & 1) != 0;
        epilogInHeader = ((header >> 21) & 1) != 0;
        if (isArm64)
        {
            epilogCount = (int)((header >> 22) & 0x1F);
            codeWords = (int)((header >> 27) & 0x1F);
        }
        else
        {
            epilogCount = (int)((header >> 23) & 0x1F);
            codeWords = (int)((header >> 28) & 0xF);
        }

        hasExtension = epilogCount == 0 && codeWords == 0;
        if (hasExtension && offset + 8 <= buffer.Length)
        {
            uint extension = buffer.ReadUInt32(offset + 4);
            epilogCount = (int)(extension & 0xFFFF);
            codeWords = (int)((extension >> 16) & 0xFF);
        }
    }

    public static int Measure(ByteBuffer buffer, int offset, bool isArm64)
    {
        if (offset + 4 > buffer.Length)
        {
            return 0;
        }

        ReadHeader(buffer, offset, isArm64, out bool hasHandler, out bool epilogInHeader, out int epilogCount, out int codeWords, out bool hasExtension);
        int size = hasExtension ? 8 : 4;
        if (!epilogInHeader)
        {
            size += epilogCount * 4;
        }

        size += codeWords * 4;
        if (hasHandler)
        {
            size += 4;
        }

        return offset + size > buffer.Length ? 0 : size;
    }

    public override void Parse()
    {
        ReadHeader(Buffer, Start, IsArm64, out bool hasHandler, out bool epilogInHeader, out int epilogCount, out int codeWords, out bool hasExtension);
        uint header = Buffer.ReadUInt32(Start);
        int functionLength = (int)(header & 0x3FFFF) * (IsArm64 ? 4 : 2);
        AddFourBytes($"Header: function length {functionLength}, {(hasHandler ? "handler, " : "")}{(epilogInHeader ? "epilog in header" : $"{epilogCount} epilog scopes")}, {codeWords} code words");
        if (hasExtension)
        {
            AddFourBytes("Extended epilog count and code words");
        }

        if (!epilogInHeader)
        {
            for (int i = 0; i < epilogCount; i++)
            {
                uint scope = Buffer.ReadUInt32(LastChildEnd);
                int startOffset = (int)(scope & 0x3FFFF) * (IsArm64 ? 4 : 2);
                AddFourBytes($"Epilog scope at +0x{startOffset:X}");
            }
        }

        if (codeWords > 0)
        {
            AddBytes(codeWords * 4, "Unwind codes");
        }

        if (hasHandler)
        {
            var handler = AddFourBytes("Exception handler RVA");
            HandlerRva = handler.Value;
            handler.Text = $"Exception handler RVA 0x{HandlerRva:X}";
        }

        Text = $"{(IsArm64 ? "ARM64" : "ARM")} unwind info (0x{Rva:X})";
    }
}
