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
        foreach (var idEntry in directory.Children.OfType<IdDirectoryEntry>())
        {
            var offset = idEntry.Offset.ReadUint32();
            if ((offset & 0x80000000) != 0)
            {
                string resourceKind = parentResourceKind;
                if (parentResourceKind == null && IdDirectoryEntry.ResourceTypes.TryGetValue(idEntry.Id.Value, out resourceKind))
                {
                    idEntry.Text = resourceKind;
                }

                var directoryOffset = offset & ~0x80000000;
                var subdirectory = new ResourceDirectory { Start = Start + (int)directoryOffset };
                Add(subdirectory);
                Process(subdirectory, resourceKind);
            }
            else
            {
                Resource resource;
                if (parentResourceKind == "Version")
                {
                    resource = new VersionResource();
                }
                else if (parentResourceKind == "Manifest")
                {
                    resource = new NativeManifestResource();
                }
                else
                {
                    resource = new Resource();
                }

                resource.Start = Start + (int)offset;

                if (parentResourceKind != null)
                {
                    resource.Text = parentResourceKind;
                    resource.Type = parentResourceKind;
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
        ParseBytes();
    }

    protected virtual void ParseBytes()
    {
        Bytes = AddBytes(Size.Value, "Bytes");
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
    public FourBytes Codepage { get; set; }
    public FourBytes Zero { get; set; }
    public Node Bytes { get; set; }

    public string Type { get; set; }
}

public class VersionResource : Resource
{
    protected override void ParseBytes()
    {
        Root = Add<VersionHeader>();
    }

    public VersionHeader Root { get; set; }
}

public class NativeManifestResource : Resource
{
    protected override void ParseBytes()
    {
        Xml = new Utf8String
        {
            Length = Size.Value
        };
        Add(Xml);
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
            var child = Add<VersionHeader>();
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
        int count = nameCount + idCount;

        for (int i = 0; i < count; i++)
        {
            uint id = Buffer.ReadUInt32(LastChildEnd);
            if ((id & 0x80000000) == 0)
            {
                var idEntry = Add<IdDirectoryEntry>($"Id: {id}");
            }
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
        [9] = "Accelerator",
        [10] = "StringData",
        [12] = "GroupCursor",
        [14] = "GroupIcon",
        [16] = "Version",
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