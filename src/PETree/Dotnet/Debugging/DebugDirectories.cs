using System.Collections.Generic;
using GuiLabs.FileFormat;

namespace GuiLabs.PEFile;

public class DebugDirectories : Node
{
    public DebugDirectories()
    {
        Text = "Debug directories";
    }

    public override void Parse()
    {
        int count = Length / 28;
        var list = new DebugDirectory[count];
        for (int i = 0; i < count; i++)
        {
            var directory = Add<DebugDirectory>();
            list[i] = directory;
        }

        Directories = list;
    }

    public IReadOnlyList<DebugDirectory> Directories { get; set; }
}
