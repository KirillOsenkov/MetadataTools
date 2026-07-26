namespace GuiLabs.FileFormat.PE;

using System.Collections.Generic;

public class ExceptionTable : Node
{
    public ExceptionTable()
    {
        Text = "Exception table";
    }

    public const int MachineAmd64 = 0x8664;
    public const int MachineIA64 = 0x200;
    public const int MachineArm = 0x01C0;
    public const int MachineThumb = 0x01C2;
    public const int MachineArmNT = 0x01C4;
    public const int MachineArm64 = 0xAA64;

    public bool IsPE32Plus { get; set; }
    public int MachineType { get; set; }
    public PEFile PEFile { get; set; }

    // The machine value arrives sign-extended from a short field
    public int Machine => MachineType & 0xFFFF;

    public bool IsArm64 => Machine == MachineArm64;
    public bool IsArm32 => Machine == MachineArm || Machine == MachineThumb || Machine == MachineArmNT;

    public override void Parse()
    {
        // RUNTIME_FUNCTION is 12 bytes on x64/Itanium (Begin, End, UnwindInfo)
        // and 8 bytes on ARM/ARM64 (Begin, UnwindData)
        int entrySize;
        if (Machine == MachineAmd64 || Machine == MachineIA64)
        {
            entrySize = 12;
        }
        else if (IsArm64 || IsArm32)
        {
            entrySize = 8;
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
                entry.PEFile = PEFile;
                AddNativeCodeRange(entry.BeginAddress.Value, entry.EndAddress.Value);
            }
            else
            {
                var entry = Add<ArmRuntimeFunction>("RUNTIME_FUNCTION");
                entry.IsArm64 = IsArm64;
                if (IsArm64)
                {
                    AddNativeCodeRange(entry.BeginAddress.Value, entry.BeginAddress.Value + entry.GetArm64FunctionLength(PEFile));
                }
            }
        }

        int trailingBytes = Length - count * entrySize;
        if (trailingBytes > 0)
        {
            AddBytes(trailingBytes, "Trailing bytes");
        }

        if (PEFile != null && Machine == MachineAmd64)
        {
            AddUnwindInfoNodes();
        }

        if (PEFile != null && IsArm64)
        {
            AddArm64UnwindInfoNodes();
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
        if (beginOffset <= 0 || endOffset <= beginOffset)
        {
            return;
        }

        // Check if this range overlaps existing nodes (e.g. RuntimeStartupStub)
        var atStart = PEFile.Find(beginOffset);
        var atEnd = PEFile.Find(endOffset - 1);
        bool startOccupied = atStart != null && atStart != PEFile && !(atStart is Section);
        bool endOccupied = atEnd != null && atEnd != PEFile && !(atEnd is Section);
        if (!startOccupied && !endOccupied)
        {
            var nativeCode = new Node
            {
                Start = beginOffset,
                Length = endOffset - beginOffset,
                Text = $"Native code (0x{beginRva:X}-0x{endRva:X})"
            };
            PEFile.Add(nativeCode);
        }
    }

    private void AddUnwindInfoNodes()
    {
        var seen = new HashSet<int>();

        for (int i = 0; i < (HasChildren ? Children.Count : 0); i++)
        {
            if (Children[i] is not RuntimeFunction rf)
            {
                continue;
            }

            int unwindRva = rf.UnwindInfoAddress.Value;
            int unwindOffset = PEFile.ResolveVirtualAddress(unwindRva);
            if (unwindOffset <= 0 || !seen.Add(unwindOffset))
            {
                continue;
            }

            // Validate: must not overlap an existing non-section node
            var existing = PEFile.Find(unwindOffset);
            if (existing != null && existing != PEFile && existing is not Section)
            {
                continue;
            }

            // Read UNWIND_INFO header (4 bytes minimum)
            if (unwindOffset + 4 > PEFile.Buffer.Length)
            {
                continue;
            }

            byte versionFlags = PEFile.Buffer.ReadByte(unwindOffset);
            int version = versionFlags & 0x7;
            int flags = (versionFlags >> 3) & 0x1F;

            // Only version 1 and 2 are defined
            if (version != 1 && version != 2)
            {
                continue;
            }

            int countOfCodes = PEFile.Buffer.ReadByte(unwindOffset + 2);

            // Size = 4 (header) + countOfCodes * 2 (codes), padded to DWORD boundary
            int size = 4 + countOfCodes * 2;
            if (countOfCodes % 2 != 0)
            {
                size += 2; // padding for DWORD alignment
            }

            const int UNW_FLAG_EHANDLER = 0x1;
            const int UNW_FLAG_UHANDLER = 0x2;
            const int UNW_FLAG_CHAININFO = 0x4;

            if ((flags & UNW_FLAG_EHANDLER) != 0 || (flags & UNW_FLAG_UHANDLER) != 0)
            {
                size += 4; // ExceptionHandler RVA
            }
            else if ((flags & UNW_FLAG_CHAININFO) != 0)
            {
                size += 12; // Chained RUNTIME_FUNCTION
            }

            if (unwindOffset + size > PEFile.Buffer.Length)
            {
                continue;
            }

            // Verify the end doesn't overlap existing nodes
            var atEnd = PEFile.Find(unwindOffset + size - 1);
            if (atEnd != null && atEnd != PEFile && atEnd is not Section)
            {
                continue;
            }

            var unwindInfo = new Node
            {
                Start = unwindOffset,
                Length = size,
                Text = $"Unwind info (0x{unwindRva:X})"
            };
            PEFile.Add(unwindInfo);
        }
    }

    private void AddArm64UnwindInfoNodes()
    {
        var seen = new HashSet<int>();

        for (int i = 0; i < (HasChildren ? Children.Count : 0); i++)
        {
            if (Children[i] is not ArmRuntimeFunction rf)
            {
                continue;
            }

            int unwindData = rf.UnwindData.Value;

            // Bits 1:0 == 0 means an RVA to an .xdata record; otherwise the
            // unwind info is packed into the field itself
            if ((unwindData & 3) != 0)
            {
                continue;
            }

            int unwindOffset = PEFile.ResolveVirtualAddress(unwindData);
            if (unwindOffset <= 0 || !seen.Add(unwindOffset))
            {
                continue;
            }

            int size = MeasureArm64UnwindInfo(unwindOffset);
            if (size <= 0 || !PEFile.IsRangeAvailable(unwindOffset, size))
            {
                continue;
            }

            var unwindInfo = new Node
            {
                Start = unwindOffset,
                Length = size,
                Text = $"ARM64 unwind info (0x{unwindData:X})"
            };
            PEFile.Add(unwindInfo);
        }
    }

    private int MeasureArm64UnwindInfo(int offset)
    {
        if (offset + 4 > PEFile.Buffer.Length)
        {
            return 0;
        }

        uint header = PEFile.Buffer.ReadUInt32(offset);
        bool hasExceptionHandler = ((header >> 20) & 1) != 0;
        bool epilogInHeader = ((header >> 21) & 1) != 0;
        int epilogCount = (int)((header >> 22) & 0x1F);
        int codeWords = (int)((header >> 27) & 0x1F);

        int size = 4;
        if (epilogCount == 0 && codeWords == 0)
        {
            if (offset + 8 > PEFile.Buffer.Length)
            {
                return 0;
            }

            uint extension = PEFile.Buffer.ReadUInt32(offset + 4);
            epilogCount = (int)(extension & 0xFFFF);
            codeWords = (int)((extension >> 16) & 0xFF);
            size += 4;
        }

        if (!epilogInHeader)
        {
            size += epilogCount * 4;
        }

        size += codeWords * 4;

        if (hasExceptionHandler)
        {
            size += 4;
        }

        return size;
    }
}

public class RuntimeFunction : Node
{
    public PEFile PEFile { get; set; }

    public override void Parse()
    {
        BeginAddress = AddFourBytes("BeginAddress");
        EndAddress = AddFourBytes("EndAddress");
        UnwindInfoAddress = AddFourBytes("UnwindInfoAddress");

        if (PEFile != null)
        {
            int beginRva = BeginAddress.Value;
            int endRva = EndAddress.Value;
            int beginOffset = PEFile.ResolveVirtualAddress(beginRva);
            int endOffset = PEFile.ResolveVirtualAddress(endRva);
            if (beginOffset > 0 && endOffset > beginOffset)
            {
                Text = $"RUNTIME_FUNCTION 0x{beginRva:X}-0x{endRva:X} ({endOffset - beginOffset} bytes)";
            }
        }
    }

    public FourBytes BeginAddress { get; set; }
    public FourBytes EndAddress { get; set; }
    public FourBytes UnwindInfoAddress { get; set; }
}

public class ArmRuntimeFunction : Node
{
    public bool IsArm64 { get; set; }

    public override void Parse()
    {
        BeginAddress = AddFourBytes("BeginAddress");
        UnwindData = AddFourBytes("UnwindData");

        int unwindData = UnwindData.Value;
        if ((unwindData & 3) != 0)
        {
            Text = $"RUNTIME_FUNCTION 0x{BeginAddress.Value:X} (packed unwind)";
        }
        else
        {
            Text = $"RUNTIME_FUNCTION 0x{BeginAddress.Value:X} (xdata 0x{unwindData:X})";
        }
    }

    public int GetArm64FunctionLength(PEFile peFile)
    {
        int unwindData = UnwindData.Value;
        if ((unwindData & 3) != 0)
        {
            // Packed: FunctionLength is bits 2..12, in units of 4 bytes
            return ((unwindData >> 2) & 0x7FF) * 4;
        }

        if (peFile == null)
        {
            return 0;
        }

        int xdataOffset = peFile.ResolveVirtualAddress(unwindData);
        if (xdataOffset <= 0 || xdataOffset + 4 > peFile.Buffer.Length)
        {
            return 0;
        }

        // .xdata: FunctionLength is bits 0..17, in units of 4 bytes
        uint header = peFile.Buffer.ReadUInt32(xdataOffset);
        return (int)(header & 0x3FFFF) * 4;
    }

    public FourBytes BeginAddress { get; set; }
    public FourBytes UnwindData { get; set; }
}
