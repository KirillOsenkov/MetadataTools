using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public class ResourceTable : Node
{
    public override void Parse()
    {
        var directory = Add<ResourceDirectory>("Root directory");
        Process(directory);
        AddRemainingPadding();
    }

    private void Process(ResourceDirectory directory, string parentResourceKind = null)
    {
        foreach (var entry in directory.Children)
        {
            uint offset;
            string resourceKind = parentResourceKind;

            if (entry is IdDirectoryEntry idEntry)
            {
                offset = idEntry.Offset.ReadUint32();
                if (parentResourceKind == null && IdDirectoryEntry.ResourceTypes.TryGetValue(idEntry.Id.Value, out var kind))
                {
                    resourceKind = kind;
                    idEntry.Text = kind;
                }
            }
            else if (entry is NameDirectoryEntry nameEntry)
            {
                offset = nameEntry.Offset.ReadUint32();

                uint nameOffset = nameEntry.NameOffset.ReadUint32() & ~0x80000000;
                int nameStart = Start + (int)nameOffset;
                int nameLength = Buffer.ReadInt16(nameStart);
                var nameNode = new ResourceNameString
                {
                    Start = nameStart,
                    Length = 2 + nameLength * 2
                };
                Add(nameNode);
                resourceKind = nameNode.Text;
                nameEntry.Text = resourceKind;
            }
            else
            {
                continue;
            }

            if ((offset & 0x80000000) != 0)
            {
                var directoryOffset = offset & ~0x80000000;
                var subdirectory = new ResourceDirectory { Start = Start + (int)directoryOffset };
                Add(subdirectory);
                Process(subdirectory, resourceKind);
            }
            else
            {
                Resource resource;
                if (resourceKind == "Version")
                {
                    resource = new VersionResource();
                }
                else if (resourceKind == "Manifest")
                {
                    resource = new NativeManifestResource();
                }
                else if (resourceKind == "MessageTable")
                {
                    resource = new MessageTableResource();
                }
                else if (resourceKind == "String")
                {
                    resource = new StringTableResource();
                }
                else if (resourceKind == "GroupIcon")
                {
                    resource = new GroupIconResource();
                }
                else
                {
                    resource = new Resource();
                }

                resource.Start = Start + (int)offset;

                if (resourceKind != null)
                {
                    resource.Text = resourceKind;
                    resource.Type = resourceKind;
                }

                Add(resource);
                AddAlignedPadding(4);
            }
        }
    }
}

public class Resource : Node
{
    public override void Parse()
    {
        RVA = AddFourBytes("RVA");
        Size = AddFourBytes("Size");
        Codepage = AddFourBytes("Codepage");
        Zero = AddFourBytes("Zero");
        var peFile = Root as PEFile;
        var start = peFile.ResolveVirtualAddress(RVA.Value);
        ParseBytes(start);
    }

    protected virtual void ParseBytes(int start)
    {
        Bytes = new BytesNode()
        {
            Start = start,
            Length = Size.Value,
            Text = $"{Type} bytes"
        };
        this.Root.Add(Bytes);
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
    public FourBytes Codepage { get; set; }
    public FourBytes Zero { get; set; }
    public Node Bytes { get; set; }

    public string Type { get; set; }
}

public class MessageTableResource : Resource
{
    protected override void ParseBytes(int start)
    {
        MessageTable = new MessageTableData
        {
            Start = start,
            Length = Size.Value
        };
        Root.Add(MessageTable);
    }

    public MessageTableData MessageTable { get; set; }
}

public class MessageTableData : Node
{
    public MessageTableData()
    {
        Text = "Message table";
    }

    public override void Parse()
    {
        NumberOfBlocks = AddFourBytes("Number of blocks");

        int blockCount = NumberOfBlocks.Value;
        if (blockCount < 0 || 4L + blockCount * 12L > Length)
        {
            return;
        }

        var blocks = new List<MessageBlock>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            blocks.Add(Add<MessageBlock>());
        }

        foreach (var block in blocks)
        {
            block.AddEntries(this);
        }

        AddRemainingPadding();
    }

    public FourBytes NumberOfBlocks { get; set; }
}

public class MessageBlock : Node
{
    public override void Parse()
    {
        LowId = AddFourBytes("Low id");
        HighId = AddFourBytes("High id");
        OffsetToEntries = AddFourBytes("Offset to entries");

        Text = $"Messages 0x{LowId.Value:X}-0x{HighId.Value:X}";
    }

    public void AddEntries(MessageTableData table)
    {
        int offset = table.Start + OffsetToEntries.Value;
        long id = (uint)LowId.Value;
        long highId = (uint)HighId.Value;

        while (id <= highId && offset + 4 <= table.End)
        {
            int entryLength = Buffer.ReadUInt16(offset);
            if (entryLength < 4 || offset + entryLength > table.End)
            {
                break;
            }

            var entry = new MessageEntry
            {
                Start = offset,
                Length = entryLength,
                MessageId = id
            };
            table.Add(entry);

            offset += entryLength;
            id++;
        }
    }

    public FourBytes LowId { get; set; }
    public FourBytes HighId { get; set; }
    public FourBytes OffsetToEntries { get; set; }
}

public class MessageEntry : Node
{
    public long MessageId { get; set; }

    public override void Parse()
    {
        EntryLength = AddTwoBytes("Length");
        Flags = AddTwoBytes("Flags");

        int textLength = EntryLength.Value - 4;
        if (textLength <= 0)
        {
            return;
        }

        var bytes = Buffer.ReadBytes(LastChildEnd, textLength);
        string message = (Flags.Value & 1) != 0
            ? System.Text.Encoding.Unicode.GetString(bytes)
            : System.Text.Encoding.ASCII.GetString(bytes);
        message = message.TrimEnd('\0', '\r', '\n');

        AddBytes(textLength, message);
        Text = $"0x{MessageId:X}: {message}";
    }

    public TwoBytes EntryLength { get; set; }
    public TwoBytes Flags { get; set; }
}

public class StringTableResource : Resource
{
    protected override void ParseBytes(int start)
    {
        StringTable = new StringTableData
        {
            Start = start,
            Length = Size.Value
        };
        Root.Add(StringTable);
    }

    public StringTableData StringTable { get; set; }
}

public class StringTableData : Node
{
    public StringTableData()
    {
        Text = "String table";
    }

    public override void Parse()
    {
        // A string table block holds 16 length-prefixed UTF-16 strings
        for (int i = 0; i < 16 && LastChildEnd + 2 <= End; i++)
        {
            int length = Buffer.ReadUInt16(LastChildEnd);
            if (2 + length * 2 > End - LastChildEnd)
            {
                break;
            }

            if (length == 0)
            {
                AddTwoBytes($"String {i}: (empty)");
                continue;
            }

            var bytes = Buffer.ReadBytes(LastChildEnd + 2, length * 2);
            string value = System.Text.Encoding.Unicode.GetString(bytes);
            AddBytes(2 + length * 2, $"String {i}: {value}");
        }

        AddRemainingPadding();
    }
}

public class GroupIconResource : Resource
{
    protected override void ParseBytes(int start)
    {
        GroupIcon = new GroupIconData
        {
            Start = start,
            Length = Size.Value
        };
        Root.Add(GroupIcon);
    }

    public GroupIconData GroupIcon { get; set; }
}

public class GroupIconData : Node
{
    public GroupIconData()
    {
        Text = "Icon group";
    }

    public override void Parse()
    {
        Reserved = AddTwoBytes("Reserved");
        IconType = AddTwoBytes("Type");
        Count = AddTwoBytes("Count");

        int count = Count.Value;
        if (count < 0 || 6 + count * 14 > Length)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            Add<GroupIconEntry>();
        }

        AddRemainingPadding();
    }

    public TwoBytes Reserved { get; set; }
    public TwoBytes IconType { get; set; }
    public TwoBytes Count { get; set; }
}

public class GroupIconEntry : Node
{
    public override void Parse()
    {
        Width = AddOneByte("Width");
        Height = AddOneByte("Height");
        ColorCount = AddOneByte("Color count");
        Reserved = AddOneByte("Reserved");
        Planes = AddTwoBytes("Planes");
        BitCount = AddTwoBytes("Bit count");
        BytesInResource = AddFourBytes("Bytes in resource");
        IconId = AddTwoBytes("Icon id");

        int width = Width.Value == 0 ? 256 : Width.Value;
        int height = Height.Value == 0 ? 256 : Height.Value;
        Text = $"Icon {IconId.Value}: {width}x{height} {BitCount.Value}bpp ({BytesInResource.Value} bytes)";
    }

    public OneByte Width { get; set; }
    public OneByte Height { get; set; }
    public OneByte ColorCount { get; set; }
    public OneByte Reserved { get; set; }
    public TwoBytes Planes { get; set; }
    public TwoBytes BitCount { get; set; }
    public FourBytes BytesInResource { get; set; }
    public TwoBytes IconId { get; set; }
}

public class VersionResource : Resource
{
    protected override void ParseBytes(int start)
    {
        VersionHeader = new VersionHeader { Start = start };
        Root.Add(VersionHeader);
    }

    public VersionHeader VersionHeader { get; set; }
}

public class NativeManifestResource : Resource
{
    protected override void ParseBytes(int start)
    {
        Xml = new Utf8String
        {
            Start = start,
            Length = Size.Value,
            Text = "Native manifest XML"
        };
        Root.Add(Xml);
    }

    public Utf8String Xml { get; set; }
}

public class VersionHeader : Node
{
    public override void Parse()
    {
        Size = AddTwoBytes("Size");
        Length = Size.Value;

        ValueLength = AddTwoBytes("Value length");
        Type = AddTwoBytes("Type");
        Key = Add<ZeroTerminatedUtf16String>("Key");
        Text = Key.Text;
        AddAlignedPadding(4);

        if (ValueLength.Value > 0)
        {
            if (Key.Text == "VS_VERSION_INFO" && ValueLength.Value == 52)
            {
                Value = Add<VSFixedFileInfo>("File info");
            }
            else if (Type.Value == 1)
            {
                Value = Add<ZeroTerminatedUtf16String>("Value");
                Text = $"{Key.Text} = {Value.Text}";
            }
            else
            {
                Value = AddBytes(ValueLength.Value, "Value");
            }

            AddAlignedPadding(4);
        }

        while (this.LastChildEnd < End)
        {
            Add<VersionHeader>();
        }
    }

    public TwoBytes Size { get; set; }
    public TwoBytes ValueLength { get; set; }
    public TwoBytes Type { get; set; }
    public ZeroTerminatedUtf16String Key { get; set; }
    public Node Value { get; set; }
}

public class VSFixedFileInfo : Node
{
    public override void Parse()
    {
        Signature = AddFourBytes("Signature");
        StrucVersion = AddFourBytes("StrucVersion");
        FileVersionMS = AddFourBytes("FileVersionMS");
        FileVersionLS = AddFourBytes("FileVersionLS");
        ProductVersionMS = AddFourBytes("ProductVersionMS");
        ProductVersionLS = AddFourBytes("ProductVersionLS");
        FileFlagsMask = AddFourBytes("FileFlagsMask");
        FileFlags = AddFourBytes("FileFlags");
        FileOS = AddFourBytes("FileOS");
        FileType = AddFourBytes("FileType");
        FileSubType = AddFourBytes("FileSubType");
        FileDateMS = AddFourBytes("FileDateMS");
        FileDateLS = AddFourBytes("FileDateLS");
    }

    public FourBytes Signature { get; set; }
    public FourBytes StrucVersion { get; set; }
    public FourBytes FileVersionMS { get; set; }
    public FourBytes FileVersionLS { get; set; }
    public FourBytes ProductVersionMS { get; set; }
    public FourBytes ProductVersionLS { get; set; }
    public FourBytes FileFlagsMask { get; set; }
    public FourBytes FileFlags { get; set; }
    public FourBytes FileOS { get; set; }
    public FourBytes FileType { get; set; }
    public FourBytes FileSubType { get; set; }
    public FourBytes FileDateMS { get; set; }
    public FourBytes FileDateLS { get; set; }
}

public class ResourceDirectory : Node
{
    public override void Parse()
    {
        ResourceDirectoryTable = Add<ResourceDirectoryTable>("Resource directory table");
        int nameCount = ResourceDirectoryTable.NameEntriesAmount.Value;
        int idCount = ResourceDirectoryTable.IdEntriesAmount.Value;

        for (int i = 0; i < nameCount; i++)
        {
            Add<NameDirectoryEntry>();
        }

        for (int i = 0; i < idCount; i++)
        {
            uint id = Buffer.ReadUInt32(LastChildEnd);
            Add<IdDirectoryEntry>($"Id: {id}");
        }
    }

    public ResourceDirectoryTable ResourceDirectoryTable { get; set; }
}

public class ResourceDirectoryTable : Node
{
    public override void Parse()
    {
        Characteristics = AddFourBytes("Characteristics");
        TimeDateStamp = AddFourBytes("TimeDate stamp");
        MajorVersion = AddTwoBytes("Major version");
        MinorVersion = AddTwoBytes("Minor version");
        NameEntriesAmount = AddTwoBytes("NameEntriesAmount");
        IdEntriesAmount = AddTwoBytes("IdEntriesAmount");
    }

    public FourBytes Characteristics { get; set; }
    public FourBytes TimeDateStamp { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public TwoBytes NameEntriesAmount { get; set; }
    public TwoBytes IdEntriesAmount { get; set; }
}

public class ResourceNameString : Node
{
    public override void Parse()
    {
        LengthField = AddTwoBytes("Length");
        int length = LengthField.Value;
        if (length > 0)
        {
            var bytes = Buffer.ReadBytes(LengthField.End, length * 2);
            Text = System.Text.Encoding.Unicode.GetString(bytes);
            AddBytes(length * 2, Text);
        }
    }

    public TwoBytes LengthField { get; set; }
}

public class IdDirectoryEntry : Node
{
    public static Dictionary<int, string> ResourceTypes = new()
    {
        [1] = "Cursor",
        [2] = "Bitmap",
        [3] = "Icon",
        [4] = "Menu",
        [5] = "Dialog",
        [6] = "String",
        [7] = "FontDir",
        [8] = "Font",
        [9] = "Accelerator",
        [10] = "StringData",
        [11] = "MessageTable",
        [12] = "GroupCursor",
        [14] = "GroupIcon",
        [16] = "Version",
        [17] = "DlgInclude",
        [19] = "PlugPlay",
        [20] = "VXD",
        [21] = "AniCursor",
        [22] = "AniIcon",
        [23] = "HTML",
        [24] = "Manifest"
    };

    public override void Parse()
    {
        Id = AddFourBytes("Id");
        Offset = AddFourBytes("Offset");
    }

    public FourBytes Id { get; set; }
    public FourBytes Offset { get; set; }
}

public class NameDirectoryEntry : Node
{
    public override void Parse()
    {
        NameOffset = AddFourBytes("Name offset");
        Offset = AddFourBytes("Offset");
    }

    public FourBytes NameOffset { get; set; }
    public FourBytes Offset { get; set; }
}