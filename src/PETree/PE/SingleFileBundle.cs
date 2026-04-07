using System.Collections.Generic;
using System.Text;

namespace GuiLabs.FileFormat.PE;

public class SingleFileBundle : Node
{
    public SingleFileBundle()
    {
        Text = "Single-file bundle";
    }

    public override void Parse()
    {
        MajorVersion = AddFourBytes("Major version");
        MinorVersion = AddFourBytes("Minor version");
        FileCount = AddFourBytes("File count");
        BundleId = Add<SerializedString>("Bundle ID");

        int major = MajorVersion.Value;
        if (major >= 2)
        {
            DepsJsonOffset = AddEightBytes("deps.json offset");
            DepsJsonSize = AddEightBytes("deps.json size");
            RuntimeConfigOffset = AddEightBytes("RuntimeConfig offset");
            RuntimeConfigSize = AddEightBytes("RuntimeConfig size");
            Flags = AddEightBytes("Flags");
        }

        int fileCount = FileCount.Value;
        var entries = new List<BundleFileEntry>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            var entry = new BundleFileEntry { MajorVersion = major };
            Add(entry);
            entries.Add(entry);
        }

        Entries = entries;
    }

    public FourBytes MajorVersion { get; set; }
    public FourBytes MinorVersion { get; set; }
    public FourBytes FileCount { get; set; }
    public SerializedString BundleId { get; set; }
    public EightBytes DepsJsonOffset { get; set; }
    public EightBytes DepsJsonSize { get; set; }
    public EightBytes RuntimeConfigOffset { get; set; }
    public EightBytes RuntimeConfigSize { get; set; }
    public EightBytes Flags { get; set; }
    public IReadOnlyList<BundleFileEntry> Entries { get; set; }
}

public class BundleFileEntry : Node
{
    public int MajorVersion { get; set; }

    public BundledFile BundledFile { get; set; }

    public override void Parse()
    {
        Offset = AddEightBytes("Offset");
        Size = AddEightBytes("Size");
        if (MajorVersion >= 6)
        {
            CompressedSize = AddEightBytes("Compressed size");
        }

        FileType = AddOneByte("File type");
        RelativePath = Add<SerializedString>("Relative path");

        long fileOffset = (long)Offset.ReadUInt64();
        long fileSize = (long)Size.ReadUInt64();
        byte type = FileType.Value;

        string typeText = type switch
        {
            0 => "Unknown",
            1 => "Assembly",
            2 => "NativeBinary",
            3 => "DepsJson",
            4 => "RuntimeConfigJson",
            5 => "Symbols",
            _ => $"Type({type})"
        };

        Text = $"{typeText}: {RelativePath.Text}";
    }

    public EightBytes Offset { get; set; }
    public EightBytes Size { get; set; }
    public EightBytes CompressedSize { get; set; }
    public OneByte FileType { get; set; }
    public SerializedString RelativePath { get; set; }
}

/// <summary>
/// A .NET BinaryWriter-style length-prefixed string (7-bit encoded integer length + UTF-8 bytes).
/// </summary>
public class SerializedString : Node
{
    public override void Parse()
    {
        // Read 7-bit encoded integer for length
        int value = 0;
        int shift = 0;
        int offset = Start;
        byte b;
        do
        {
            b = Buffer.ReadByte(offset);
            value |= (b & 0x7F) << shift;
            shift += 7;
            offset++;
        }
        while ((b & 0x80) != 0);

        int headerLength = offset - Start;
        AddBytes(headerLength, $"String length: {value}");

        if (value > 0)
        {
            var bytes = Buffer.ReadBytes(offset, value);
            Text = Encoding.UTF8.GetString(bytes);
            AddBytes(value, Text);
        }
        else
        {
            Text = "";
        }
    }
}

public class BundleMarker : Node
{
    public BundleMarker()
    {
        Text = "Bundle marker";
        Length = 8 + 32; // 8-byte offset + 32-byte signature
    }

    public override void Parse()
    {
        HeaderOffset = AddEightBytes("Header offset");
        Signature = AddBytes(32, "Bundle signature");
    }

    public EightBytes HeaderOffset { get; set; }
    public Node Signature { get; set; }
}

public class BundledFile : Node
{
    public BundleFileEntry Entry { get; set; }

    public override void Parse()
    {
    }
}
