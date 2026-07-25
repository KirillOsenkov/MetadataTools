namespace GuiLabs.FileFormat.PE;

public class TLSDirectory : Node
{
    public TLSDirectory()
    {
        Text = "Thread Local Storage table";
    }

    public bool IsPE32Plus { get; set; }
    public PEFile PEFile { get; set; }

    public override void Parse()
    {
        int pointerSize = IsPE32Plus ? 8 : 4;
        int expectedSize = pointerSize * 4 + 8;
        if (Length < expectedSize)
        {
            return;
        }

        StartAddressOfRawData = AddPointer("Start address of raw data");
        EndAddressOfRawData = AddPointer("End address of raw data");
        AddressOfIndex = AddPointer("Address of index");
        AddressOfCallbacks = AddPointer("Address of callbacks");
        SizeOfZeroFill = AddFourBytes("Size of zero fill");
        Characteristics = AddFourBytes("Characteristics");

        int remaining = Length - expectedSize;
        if (remaining > 0)
        {
            AddBytes(remaining, "Extended TLS data");
        }

        AddCallbacks();
    }

    private BytesNode AddPointer(string text)
    {
        var pointer = AddFourOrEightBytes(IsPE32Plus);
        pointer.Text = text;
        return pointer;
    }

    private void AddCallbacks()
    {
        if (PEFile == null)
        {
            return;
        }

        ulong imageBase = IsPE32Plus
            ? Buffer.ReadUInt64(PEFile.OptionalHeader.WindowsFields.ImageBase.Start)
            : Buffer.ReadUInt32(PEFile.OptionalHeader.WindowsFields.ImageBase.Start);
        ulong callbacksVA = IsPE32Plus
            ? Buffer.ReadUInt64(AddressOfCallbacks.Start)
            : Buffer.ReadUInt32(AddressOfCallbacks.Start);

        if (callbacksVA < imageBase || callbacksVA - imageBase > int.MaxValue)
        {
            return;
        }

        int callbacksRva = (int)(callbacksVA - imageBase);
        var section = PEFile.GetSectionAtVirtualAddress(callbacksRva);
        int callbacksOffset = PEFile.ResolveVirtualAddress(callbacksRva);
        if (section == null || callbacksOffset <= 0)
        {
            return;
        }

        int pointerSize = IsPE32Plus ? 8 : 4;
        long sectionEnd = (long)section.PointerToRawData.Value + section.SizeOfRawData.Value;
        int callbackCount = 0;
        bool foundTerminator = false;
        for (int offset = callbacksOffset; offset <= sectionEnd - pointerSize; offset += pointerSize)
        {
            callbackCount++;
            ulong callback = IsPE32Plus
                ? Buffer.ReadUInt64(offset)
                : Buffer.ReadUInt32(offset);
            if (callback == 0)
            {
                foundTerminator = true;
                break;
            }
        }

        if (!foundTerminator)
        {
            return;
        }

        int tableSize = callbackCount * pointerSize;
        if (!PEFile.IsRangeAvailable(callbacksOffset, tableSize))
        {
            return;
        }

        var callbacks = new TLSCallbackTable
        {
            Start = callbacksOffset,
            Length = tableSize,
            IsPE32Plus = IsPE32Plus,
            Text = $"TLS callback table ({callbackCount - 1} callbacks)"
        };
        PEFile.Add(callbacks);
    }

    public BytesNode StartAddressOfRawData { get; set; }
    public BytesNode EndAddressOfRawData { get; set; }
    public BytesNode AddressOfIndex { get; set; }
    public BytesNode AddressOfCallbacks { get; set; }
    public FourBytes SizeOfZeroFill { get; set; }
    public FourBytes Characteristics { get; set; }
}

public class TLSCallbackTable : Node
{
    public bool IsPE32Plus { get; set; }

    public override void Parse()
    {
        int pointerSize = IsPE32Plus ? 8 : 4;
        int count = Length / pointerSize;
        for (int i = 0; i < count; i++)
        {
            var callback = AddFourOrEightBytes(IsPE32Plus);
            callback.Text = i == count - 1 ? "Terminator" : "Callback";
        }
    }
}
