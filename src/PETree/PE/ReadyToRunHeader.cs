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
        CoreHeader = Add<ReadyToRunCoreHeader>("Core header");
        Text = $"ReadyToRun header {MajorVersion.Value}.{MinorVersion.Value}";
    }

    public FourBytes Signature { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public ReadyToRunCoreHeader CoreHeader { get; set; }
    public IReadOnlyList<ReadyToRunSection> Sections => CoreHeader?.Sections;
}

/// <summary>
/// Flags and section list. The image header holds one, and in a composite
/// image every component assembly has its own.
/// </summary>
public class ReadyToRunCoreHeader : Node
{
    public override void Parse()
    {
        Flags = AddFourBytes("Flags");
        Flags.Text = $"Flags: {DescribeFlags(Flags.Value)}";
        NumberOfSections = AddFourBytes("Number of sections");

        int count = NumberOfSections.Value;
        var list = new List<ReadyToRunSection>();
        if (count >= 0 && count <= 1024 && (long)LastChildEnd + count * 12L <= Buffer.Length)
        {
            for (int i = 0; i < count; i++)
            {
                list.Add(Add<ReadyToRunSection>());
            }
        }

        Sections = list;
    }

    private static string DescribeFlags(int flags)
    {
        var names = new List<string>();
        (int bit, string name)[] known =
        [
            (0x01, "PlatformNeutralSource"),
            (0x02, "Composite"),
            (0x04, "Partial"),
            (0x08, "NonSharedPInvokeStubs"),
            (0x10, "EmbeddedMsil"),
            (0x20, "Component"),
            (0x40, "MultiModuleVersionBubble"),
            (0x80, "UnrelatedR2RCode"),
            (0x100, "PlatformNativeImage")
        ];
        foreach (var (bit, name) in known)
        {
            if ((flags & bit) != 0)
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? $"0x{flags:X}" : string.Join(", ", names);
    }

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
            107 => "AvailableTypes (old format)",
            108 => "AvailableTypes",
            109 => "InstanceMethodEntryPoints",
            110 => "InliningInfo",
            111 => "ProfileDataInfo",
            112 => "ManifestMetadata",
            113 => "AttributePresence",
            114 => "InliningInfo2",
            115 => "ComponentAssemblies",
            116 => "OwnerCompositeExecutable",
            117 => "PgoInstrumentationData",
            118 => "ManifestAssemblyMvids",
            119 => "CrossModuleInlineInfo",
            120 => "HotColdMap",
            121 => "MethodIsGenericMap",
            122 => "EnclosingTypeMap",
            123 => "TypeGenericInfoMap",
            124 => "ExternalTypeMaps",
            125 => "ProxyTypeMaps",
            126 => "TypeMapAssemblyTargets",
            127 => "WasmAsyncResumeInfo",
            128 => "AuxiliaryModuleMetadata",
            200 => "StringTable",
            201 => "GCStaticRegion",
            202 => "ThreadStaticRegion",
            203 => "InterfaceDispatchTable",
            204 => "TypeManagerIndirection",
            205 => "EagerCctor",
            206 => "FrozenObjectRegion",
            207 => "DehydratedData",
            208 => "ThreadStaticOffsetRegion",
            209 => "InterfaceDispatchCellInfoRegion",
            210 => "InterfaceDispatchCellRegion",
            211 => "LoopHijackFlag",
            212 => "ImportAddressTables",
            213 => "ModuleInitializerList",
            214 => "GvmDispatchCellInfoRegion",
            215 => "GvmDispatchCellRegion",
            >= 300 and <= 399 => GetReflectionBlobName(type - 300),
            _ => $"Section type {type}"
        };
    }

    public static string GetReflectionBlobName(int blob)
    {
        return blob switch
        {
            1 => "TypeMap",
            2 => "ArrayMap",
            3 => "PointerTypeMap",
            4 => "FunctionPointerTypeMap",
            6 => "InvokeMap",
            7 => "VirtualInvokeMap",
            8 => "CommonFixupsTable",
            9 => "FieldAccessMap",
            10 => "CCtorContextMap",
            11 => "ByRefTypeMap",
            13 => "EmbeddedMetadata",
            15 => "UnboxingAndInstantiatingStubMap",
            16 => "StructMarshallingStubMap",
            17 => "DelegateMarshallingStubMap",
            18 => "GenericVirtualMethodTable",
            19 => "InterfaceGenericVirtualMethodTable",
            21 => "TypeTemplateMap",
            22 => "GenericMethodsTemplateMap",
            24 => "ResourceIndex",
            25 => "ResourceData",
            26 => "StackTraceEmbeddedMetadata",
            27 => "StackTraceMethodRvaToTokenMapping",
            28 => "StackTraceLineNumbers",
            29 => "StackTraceDocuments",
            30 => "NativeLayoutInfo",
            31 => "NativeReferences",
            32 => "GenericsHashtable",
            33 => "NativeStatics",
            34 => "StaticsInfoHashtable",
            35 => "GenericMethodsHashtable",
            36 => "ExactMethodInstantiationsHashtable",
            _ => $"Blob {blob}"
        };
    }

    public FourBytes SectionType { get; set; }
    public FourBytes RVA { get; set; }
    public FourBytes Size { get; set; }
}

/// <summary>
/// The .NET native format sparse array: a header, one offset per block of
/// 16 elements, then the blocks.
/// </summary>
public class NativeArray : Node
{
    public override void Parse()
    {
        var cursor = new FieldCursor(Buffer, Start, this);
        uint header = cursor.CompressedUInt32("Header");
        if (cursor.Failed)
        {
            return;
        }

        int count = (int)(header >> 2);
        int entrySize = 1 << (int)(header & 3);
        long indexSize = (long)((count + 15) / 16) * entrySize;
        if (indexSize > 0 && indexSize <= Remaining)
        {
            AddBytes((int)indexSize, $"Block index ({(count + 15) / 16} entries of {entrySize} bytes)");
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Elements");
        }

        ElementCount = count;
    }

    public int ElementCount { get; set; }
}

/// <summary>
/// The .NET native format hashtable: a header byte, the bucket offsets, then
/// the entries.
/// </summary>
public class NativeHashtable : Node
{
    public override void Parse()
    {
        if (Length < 1)
        {
            return;
        }

        var header = AddOneByte("Header");
        int bucketCount = 1 << (header.Value >> 2);
        int entrySize = 1 << (header.Value & 3);
        header.Text = $"Header: {bucketCount} buckets, {entrySize}-byte offsets";
        long tableSize = (long)(bucketCount + 1) * entrySize;
        if (tableSize <= Remaining)
        {
            AddBytes((int)tableSize, "Bucket offsets");
        }

        if (Remaining > 0)
        {
            AddBytes(Remaining, "Entries");
        }
    }
}

public class GcInfo : Node
{
    public GcInfo()
    {
        Text = "GC info";
    }
}

public class ReadyToRunSignature : Node
{
}
