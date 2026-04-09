namespace GuiLabs.FileFormat.PE;

using System.Collections.Generic;

public class ExceptionTable : Node
{
    public ExceptionTable()
    {
        Text = "Exception table";
    }

    public bool IsPE32Plus { get; set; }
    public int MachineType { get; set; }
    public PEFile PEFile { get; set; }

    public override void Parse()
    {
        if (!IsPE32Plus)
        {
            return;
        }

        int entrySize = 12; // RUNTIME_FUNCTION: BeginAddress(4) + EndAddress(4) + UnwindInfoAddress(4)
        int count = Length / entrySize;

        for (int i = 0; i < count; i++)
        {
            var entry = Add<RuntimeFunction>("RUNTIME_FUNCTION");
            entry.PEFile = PEFile;

            if (PEFile != null)
            {
                int beginRva = entry.BeginAddress.Value;
                int endRva = entry.EndAddress.Value;

                // Both RVAs must resolve to the same section
                var beginSection = PEFile.GetSectionAtVirtualAddress(beginRva);
                var endSection = PEFile.GetSectionAtVirtualAddress(endRva);
                if (beginSection == null || endSection == null || beginSection != endSection)
                {
                    continue;
                }

                int beginOffset = PEFile.ResolveVirtualAddressInSection(beginRva, beginSection);
                int endOffset = PEFile.ResolveVirtualAddressInSection(endRva, endSection);
                if (beginOffset > 0 && endOffset > beginOffset)
                {
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
            }
        }

        // Add unwind info nodes for unique UnwindInfoAddress values (x64 only)
        if (PEFile != null && MachineType == 0x8664)
        {
            AddUnwindInfoNodes();
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
