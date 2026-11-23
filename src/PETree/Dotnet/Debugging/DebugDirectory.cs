using GuiLabs.Utilities;

namespace GuiLabs.FileFormat.PE.Dotnet;

public class DebugDirectoryEntry : Node
{
    public DebugDirectoryEntry()
    {
        Text = "Debug directory entry";
    }
}

public class RSDS : DebugDirectoryEntry
{
    public RSDS()
    {
        Text = "CodeView RSDS";
    }

    public override void Parse()
    {
        RSDSSignature = AddFourBytes("RSDS Signature");
        Guid = Add<SixteenBytes>("Guid");
        Age = AddFourBytes("Age");
        Path = Add<ZeroTerminatedString>("Path");
    }

    public FourBytes RSDSSignature { get; set; }
    public SixteenBytes Guid { get; set; }
    public FourBytes Age { get; set; }
    public ZeroTerminatedString Path { get; set; }
}

public class DebugDirectory : Node
{
    public override void Parse()
    {
        Characteristics = AddFourBytes("Characteristics");
        Timestamp = AddFourBytes("Timestamp");
        MajorVersion = AddTwoBytes("Major version");
        MinorVersion = AddTwoBytes("Minor version");
        Type = AddFourBytes("Type");
        SizeOfData = AddFourBytes("Size of data");
        AddressOfRawData = AddFourBytes("Address of raw data");
        PointerToRawData = AddFourBytes("Pointer to raw data");

        DirectoryType = (ImageDebugType)Type.Value;

        Text = $"{DirectoryType}";
    }

    public FourBytes Characteristics { get; set; }
    public FourBytes Timestamp { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public FourBytes Type { get; set; }
    public FourBytes SizeOfData { get; set; }
    public FourBytes AddressOfRawData { get; set; }
    public FourBytes PointerToRawData { get; set; }

    public ImageDebugType DirectoryType { get; set; }

    public enum ImageDebugType : uint
    {
        Unknown = 0,
        Coff = 1,
        CodeView = 2,
        Fpo = 3,
        Misc = 4,
        Exception = 5,
        Fixup = 6,
        OmapToSrc = 7,
        OmapFromSrc = 8,
        Borland = 9,
        Reserved10 = 10,
        Clsid = 11,
        VcFeature = 12,
        Pogo = 13,
        Iltcg = 14,
        Mpx = 15,
        Reproducible = 16,
        EmbeddedPortablePdb = 17,
        Spgo = 18,
        PdbChecksum = 19,
        ExtendedDllCharacteristics = 20,
        R2RPerfMap = 21,
    }

    /// <summary>
    /// Creates the node for the data of a debug directory entry. Start and
    /// Length are set by the caller.
    /// </summary>
    public Node CreateDataNode(ByteBuffer buffer, int start, int size)
    {
        uint signature = size >= 4 ? buffer.ReadUInt32(start) : 0;
        switch (DirectoryType)
        {
            case ImageDebugType.Coff:
                return new CoffDebugData();
            case ImageDebugType.CodeView:
                if (signature == 0x53445352)
                {
                    return new RSDS();
                }

                if (signature == 0x3031424E)
                {
                    return new CodeViewNB10();
                }

                return new CodeViewData();
            case ImageDebugType.Fpo:
                return new TableDebugData
                {
                    Text = "FPO data",
                    RecordText = "FPO_DATA",
                    Fields =
                    [
                        ("Function start offset", 4),
                        ("Function size", 4),
                        ("Locals size in dwords", 4),
                        ("Parameters size in dwords", 2),
                        ("Prolog size, saved registers and flags", 2)
                    ]
                };
            case ImageDebugType.Misc:
                return new MiscDebugData();
            case ImageDebugType.Fixup:
                return new TableDebugData
                {
                    Text = "Fixup data",
                    RecordText = "Fixup",
                    Fields = [("Type", 2), ("Extra", 2), ("RVA", 4), ("Target RVA", 4)]
                };
            case ImageDebugType.OmapToSrc:
            case ImageDebugType.OmapFromSrc:
                return new TableDebugData
                {
                    Text = DirectoryType == ImageDebugType.OmapToSrc ? "OMAP to source" : "OMAP from source",
                    RecordText = "OMAP entry",
                    Fields = [("RVA", 4), ("Mapped RVA", 4)]
                };
            case ImageDebugType.Clsid:
                return new TableDebugData { Text = "CLSID", RecordText = "CLSID", Fields = [("CLSID", 16)] };
            case ImageDebugType.Reserved10:
                return new TableDebugData { Text = "BBT data", RecordText = "Value", Fields = [("Value", 4)] };
            case ImageDebugType.VcFeature:
                return new VcFeatureDebugData();
            case ImageDebugType.Pogo:
                return new PogoDebugData();
            case ImageDebugType.Reproducible:
                return new ReproducibleDebugData();
            case ImageDebugType.EmbeddedPortablePdb:
                return new EmbeddedPdb();
            case ImageDebugType.PdbChecksum:
                return new PdbChecksumDebugData();
            case ImageDebugType.ExtendedDllCharacteristics:
                return new ExtendedDllCharacteristicsDebugData();
            case ImageDebugType.R2RPerfMap:
                return new R2RPerfMapDebugData();
            case ImageDebugType.Exception:
                return new DebugDirectoryEntry { Text = "Exception data (copy of the function table)" };
            case ImageDebugType.Borland:
                return new DebugDirectoryEntry { Text = "Borland debug data" };
            case ImageDebugType.Iltcg:
                return new DebugDirectoryEntry { Text = "ILTCG data" };
            case ImageDebugType.Mpx:
                return new DebugDirectoryEntry { Text = "MPX data" };
            case ImageDebugType.Spgo:
                return new DebugDirectoryEntry { Text = "Sample PGO data" };
            default:
                return new DebugDirectoryEntry { Text = $"Debug data ({DirectoryType})" };
        }
    }
}

/// <summary>
/// Debug data that is an array of fixed-size records.
/// </summary>
public class TableDebugData : DebugDirectoryEntry
{
    public (string name, int size)[] Fields { get; set; }
    public string RecordText { get; set; }

    public override void Parse()
    {
        int recordSize = 0;
        foreach (var field in Fields)
        {
            recordSize += field.size;
        }

        int count = Length / recordSize;
        for (int i = 0; i < count; i++)
        {
            Add(new Record { Fields = Fields, Text = RecordText });
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Trailing bytes");
        }

        Text = $"{Text} ({count})";
    }
}

/// <summary>
/// CodeView record of the PDB 2.0 format: the PDB is identified by a
/// timestamp and an age.
/// </summary>
public class CodeViewNB10 : DebugDirectoryEntry
{
    public override void Parse()
    {
        AddFourBytes("NB10 signature");
        AddFourBytes("Offset");
        AddFourBytes("Timestamp");
        AddFourBytes("Age");
        if (Remaining > 0)
        {
            var bytes = Buffer.ReadBytes(LastChildEnd, Remaining);
            string path = bytes.ReadZeroTerminatedString();
            AddBytes(Remaining, $"Path: {path}");
            Text = $"CodeView NB10: {path}";
        }
        else
        {
            Text = "CodeView NB10";
        }
    }
}

/// <summary>
/// CodeView debug information stored in the image itself (NB05, NB08, NB09,
/// NB11 and others). Only the header and the trailer are labelled; the
/// subsection directory and its contents are left as one range.
/// </summary>
public class CodeViewData : DebugDirectoryEntry
{
    public override void Parse()
    {
        if (Length < 8)
        {
            Text = "CodeView data";
            return;
        }

        string signature = System.Text.Encoding.ASCII.GetString(Buffer.ReadBytes(Start, 4));
        bool isKnown = signature.StartsWith("NB");
        if (!isKnown)
        {
            Text = "CodeView data";
            return;
        }

        AddFourBytes($"{signature} signature");
        AddFourBytes("Subsection directory offset");

        // The same signature and the offset of the data from the end of the
        // file are repeated at the very end
        bool hasTrailer = Length >= 16 &&
            System.Text.Encoding.ASCII.GetString(Buffer.ReadBytes(End - 8, 4)) == signature;
        int bodyLength = Remaining - (hasTrailer ? 8 : 0);
        if (bodyLength > 0)
        {
            AddBytes(bodyLength, "Symbol and type data");
        }

        if (hasTrailer)
        {
            AddFourBytes($"{signature} trailer signature");
            AddFourBytes("Offset from end of file");
        }

        Text = $"CodeView {signature} debug information";
    }
}

/// <summary>
/// IMAGE_COFF_SYMBOLS_HEADER. The symbol table and the line numbers it
/// locates are inside the data of this entry.
/// </summary>
public class CoffDebugData : DebugDirectoryEntry
{
    public override void Parse()
    {
        if (Length < 32)
        {
            return;
        }

        var header = new Record
        {
            Fields =
            [
                ("Number of symbols", 4),
                ("Offset of first symbol", 4),
                ("Number of line numbers", 4),
                ("Offset of first line number", 4),
                ("RVA of first byte of code", 4),
                ("RVA of last byte of code", 4),
                ("RVA of first byte of data", 4),
                ("RVA of last byte of data", 4)
            ],
            Text = "COFF symbols header"
        };
        Add(header);

        int lineNumberCount = header.ReadInt32(2);
        int lineNumberOffset = header.ReadInt32(3);
        if (lineNumberCount > 0 && lineNumberOffset >= 32 && (long)lineNumberOffset + lineNumberCount * 6L <= Length)
        {
            var lineNumbers = new RecordTable
            {
                Start = Start + lineNumberOffset,
                Length = lineNumberCount * 6,
                Count = lineNumberCount,
                Fields = [("Symbol table index or virtual address", 4), ("Line number", 2)],
                RecordText = "Line number"
            };
            Add(lineNumbers);
            lineNumbers.Text = $"COFF line numbers ({lineNumberCount})";
        }

        Text = "COFF debug information";
    }
}

public class MiscDebugData : DebugDirectoryEntry
{
    public override void Parse()
    {
        while (Remaining >= 12)
        {
            int recordStart = LastChildEnd;
            int dataType = Buffer.ReadInt32(recordStart);
            int recordLength = Buffer.ReadInt32(recordStart + 4);
            bool isUnicode = Buffer.ReadByte(recordStart + 8) != 0;
            if (recordLength < 12 || recordLength > Remaining)
            {
                break;
            }

            var record = new Record
            {
                Fields =
                [
                    ("Data type", 4),
                    ("Length", 4),
                    ("Unicode", 1),
                    ("Reserved", 3),
                    ("Data", recordLength - 12)
                ]
            };
            Add(record);

            var data = Buffer.ReadBytes(recordStart + 12, recordLength - 12);
            string value = isUnicode
                ? System.Text.Encoding.Unicode.GetString(data)
                : System.Text.Encoding.ASCII.GetString(data);
            int terminator = value.IndexOf('\0');
            if (terminator >= 0)
            {
                value = value.Substring(0, terminator);
            }

            record.Text = dataType == 1 ? $"Executable name: {value}" : $"Misc data type {dataType}: {value}";
            Text = $"Misc debug data: {value}";
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Trailing bytes");
        }
    }
}

public class ReproducibleDebugData : DebugDirectoryEntry
{
    public override void Parse()
    {
        Text = "Reproducible build hash";
        if (Length < 4)
        {
            return;
        }

        var length = AddFourBytes("Hash length");
        int hashLength = System.Math.Min(length.Value, Remaining);
        if (hashLength > 0)
        {
            AddBytes(hashLength, "Hash");
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Trailing bytes");
        }
    }
}

public class R2RPerfMapDebugData : DebugDirectoryEntry
{
    public override void Parse()
    {
        Text = "R2R perf map";
        if (Length < 24)
        {
            return;
        }

        AddFourBytes("Magic (R2RM)");
        Add<SixteenBytes>("Signature");
        AddFourBytes("Version");
        if (Remaining > 0)
        {
            string path = Buffer.ReadBytes(LastChildEnd, Remaining).ReadZeroTerminatedString();
            AddBytes(Remaining, $"Path: {path}");
            Text = $"R2R perf map: {path}";
        }
    }
}

public class PogoDebugData : DebugDirectoryEntry
{
    public PogoDebugData()
    {
        Text = "POGO data (section contributions)";
    }

    public override void Parse()
    {
        Signature = AddFourBytes("Signature");

        while (LastChildEnd + 8 < End)
        {
            Add<PogoEntry>();
        }

        AddRemainingPadding();
    }

    public FourBytes Signature { get; set; }
}

public class PogoEntry : Node
{
    public override void Parse()
    {
        RVA = AddFourBytes("RVA");
        Size = AddFourBytes("Size");
        Name = Add<ZeroTerminatedString>("Name");

        int aligned = (LastChildEnd - Start + 3) & ~3;
        if (Start + aligned > LastChildEnd)
        {
            AddPadding(Start + aligned - LastChildEnd);
        }

        Text = $"{Name.Text} RVA 0x{RVA.Value:X} ({Size.Value} bytes)";
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
    public ZeroTerminatedString Name { get; set; }
}

public class PdbChecksumDebugData : DebugDirectoryEntry
{
    public PdbChecksumDebugData()
    {
        Text = "PDB checksum";
    }

    public override void Parse()
    {
        AlgorithmName = Add<ZeroTerminatedString>("Algorithm");
        int hashLength = End - LastChildEnd;
        if (hashLength > 0)
        {
            Checksum = AddBytes(hashLength, "Checksum");
        }

        Text = $"PDB checksum ({AlgorithmName.Text})";
    }

    public ZeroTerminatedString AlgorithmName { get; set; }
    public Node Checksum { get; set; }
}

public class ExtendedDllCharacteristicsDebugData : DebugDirectoryEntry
{
    public ExtendedDllCharacteristicsDebugData()
    {
        Text = "Extended DLL characteristics";
    }

    public override void Parse()
    {
        Characteristics = AddFourBytes("Characteristics");

        int value = Characteristics.Value;
        var flags = new System.Collections.Generic.List<string>();
        if ((value & 0x01) != 0)
        {
            flags.Add("CET compatible");
        }

        if ((value & 0x40) != 0)
        {
            flags.Add("Forward CFI compatible");
        }

        if ((value & 0x80) != 0)
        {
            flags.Add("Hotpatch compatible");
        }

        if (flags.Count > 0)
        {
            Text = $"Extended DLL characteristics: {string.Join(", ", flags)}";
        }
    }

    public FourBytes Characteristics { get; set; }
}

public class VcFeatureDebugData : DebugDirectoryEntry
{
    public VcFeatureDebugData()
    {
        Text = "VC feature data";
    }

    public override void Parse()
    {
        AddFourBytes("Pre-VC++ 11 count");
        AddFourBytes("C/C++ count");
        AddFourBytes("/GS count");
        AddFourBytes("/sdl count");
        AddFourBytes("GuardN count");
        AddRemainingPadding();
    }
}
