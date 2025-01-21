using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public class ResourceTable : Node
{
    public override void Parse()
    {
        var directory = Add<ResourceDirectory>("Root directory");
        Process(directory, isRoot: true);
    }

    private void Process(ResourceDirectory directory, bool isRoot = false)
    {
        foreach (var idEntry in directory.Children.OfType<IdDirectoryEntry>())
        {
            var offset = idEntry.Offset.ReadUint32();
            if ((offset & 0x80000000) != 0)
            {
                if (isRoot && IdDirectoryEntry.ResourceTypes.TryGetValue(idEntry.Id.Value, out var text))
                {
                    idEntry.Text = text;
                }

                var directoryOffset = offset & ~0x80000000;
                var subdirectory = new ResourceDirectory { Start = Start + (int)directoryOffset };
                Add(subdirectory);
                Process(subdirectory);
            }
            else
            {
                var resource = new Resource { Start = Start + (int)offset };
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
        Bytes = AddBytes(Size.Value, "Bytes");
    }

    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
    public FourBytes Codepage { get; set; }
    public FourBytes Zero { get; set; }
    public Node Bytes { get; set; }
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