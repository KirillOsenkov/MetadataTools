namespace GuiLabs.FileFormat.PE.Dotnet;

public class FatMethod : Node
{
    public FatMethod()
    {
        Text = "Fat method";
    }

    public override void Parse()
    {
        Header = AddTwoBytes("Header");
        MaxStack = AddTwoBytes("Max stack");
        CodeSize = AddFourBytes("Code size");
        LocalVarSignatureToken = AddFourBytes("Local variable signature token");
        ILCode = new Node { Length = CodeSize.Value, Text = "IL instructions" };
        Add(ILCode);

        if ((Header.Value & 8) != 0)
        {
            AddSection();
        }
    }

    private int GetRemaining()
    {
        // Use the parent section's bounds (not the whole buffer)
        // to avoid growing beyond the containing section.
        int limit = Parent != null ? Parent.End : (int)Buffer.Length;
        return limit - LastChildEnd;
    }

    private void AddSection()
    {
        AddAlignedPadding(4);

        if (GetRemaining() < 1)
        {
            return;
        }

        const byte eh_table = 0x01;
        const byte fat_format = 0x40;
        const byte more_sects = 0x80;

        var flags = AddOneByte("Flags");

        // Per ECMA-335, the Kind field (bits 0-5) must be 1 (EHTable).
        // If it's not, this isn't a valid exception section — likely native
        // code being misinterpreted as IL.
        if ((flags.Value & 0x3F) != eh_table)
        {
            return;
        }

        if ((flags.Value & fat_format) == 0)
        {
            AddSmallSection();
        }
        else
        {
            AddFatSection();
        }

        if ((flags.Value & more_sects) != 0)
        {
            AddSection();
        }
    }

    private void AddFatSection()
    {
        var integer = AddThreeBytes("Size");
        int sectionSize = integer.Value - 4;
        int remaining = GetRemaining();
        if (sectionSize <= 0 || sectionSize > remaining)
        {
            if (remaining > 0)
            {
                AddBytes(remaining, "Fat section (truncated)");
            }

            return;
        }

        AddBytes(sectionSize, "Fat section");
    }

    private void AddSmallSection()
    {
        var count = AddOneByte("Count").Value;
        AddTwoBytes("Ignored");
        int sectionSize = count - 4;
        int remaining = GetRemaining();
        if (sectionSize <= 0 || sectionSize > remaining)
        {
            if (remaining > 0)
            {
                AddBytes(remaining, "Small section (truncated)");
            }

            return;
        }

        AddBytes(sectionSize, "Small section");
    }

    public TwoBytes Header { get; set; }
    public TwoBytes MaxStack { get; set; }
    public FourBytes CodeSize { get; set; }
    public FourBytes LocalVarSignatureToken { get; set; }
    public Node ILCode { get; set; }
}
