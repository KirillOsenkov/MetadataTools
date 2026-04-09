namespace GuiLabs.FileFormat.PE;

public class ExceptionTable : Node
{
    public ExceptionTable()
    {
        Text = "Exception table";
    }

    public bool IsPE32Plus { get; set; }
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
                int beginOffset = PEFile.ResolveVirtualAddress(beginRva);
                int endOffset = PEFile.ResolveVirtualAddress(endRva);
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
