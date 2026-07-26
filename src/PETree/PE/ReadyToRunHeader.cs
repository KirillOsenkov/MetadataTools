using System.Collections.Generic;

namespace GuiLabs.FileFormat.PE;

public class ReadyToRunHeader : Node
{
    public ReadyToRunHeader()
    {
        Text = "ReadyToRun header";
    }

    public override void Parse()
    {
        Signature = AddFourBytes("Signature (RTR)");
        MajorVersion = AddTwoBytes("Major version");
        MinorVersion = AddTwoBytes("Minor version");
        Flags = AddFourBytes("Flags");
        NumberOfSections = AddFourBytes("Number of sections");

        int count = NumberOfSections.Value;
        if (count < 0 || 16L + count * 12L > Length)
        {
            return;
        }

        var list = new List<ReadyToRunSection>(count);
        for (int i = 0; i < count; i++)
        {
            var section = Add<ReadyToRunSection>();
            list.Add(section);
        }

        Sections = list;
        Text = $"ReadyToRun header {MajorVersion.Value}.{MinorVersion.Value}";
    }

    public FourBytes Signature { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public FourBytes Flags { get; set; }
    public FourBytes NumberOfSections { get; set; }
    public IReadOnlyList<ReadyToRunSection> Sections { get; set; }
}

public class ReadyToRunSection : Node
{
    public override void Parse()
    {
        SectionType = AddFourBytes("Section type");
        RVA = AddFourBytes("RVA");
        Size = AddFourBytes("Size");

        Text = $"{GetSectionTypeName(SectionType.Value)} ({Size.Value} bytes)";
    }

    public static string GetSectionTypeName(int type)
    {
        return type switch
        {
            100 => "CompilerIdentifier",
            101 => "ImportSections",
            102 => "RuntimeFunctions",
            103 => "MethodDefEntryPoints",
            104 => "ExceptionInfo",
            105 => "DebugInfo",
            106 => "DelayLoadMethodCallThunks",
            107 => "AvailableTypes",
            108 => "InstanceMethodEntryPoints",
            109 => "InliningInfo",
            110 => "ProfileDataInfo",
            111 => "ManifestMetadata",
            112 => "AttributePresence",
            113 => "InliningInfo2",
            114 => "ComponentAssemblies",
            115 => "OwnerCompositeExecutable",
            116 => "PgoInstrumentationData",
            117 => "ManifestAssemblyMvids",
            118 => "CrossModuleInlineInfo",
            119 => "HotColdMap",
            120 => "MethodIsGenericMap",
            121 => "EnclosingTypeMap",
            122 => "TypeGenericInfoMap",
            _ => $"Section type {type}"
        };
    }

    public FourBytes SectionType { get; set; }
    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
}
