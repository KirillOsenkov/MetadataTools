// Portions of this file taken from https://github.com/jbevain/cecil
// Copyright (C) Jb Evain

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GuiLabs.Utilities;

namespace GuiLabs.FileFormat.PE.Dotnet;

public class Metadata : Node
{
    public Metadata()
    {
        Text = "Metadata";
    }

    public override void Parse()
    {
        BSJB = AddFourBytes("BSJB signature");
        MajorVersion = AddTwoBytes("Major version");
        MinorVersion = AddTwoBytes("Minor version");
        Reserved = AddFourBytes("Reserved");
        RuntimeVersion = Add<ZeroTerminatedStringLengthPrefix32>();
        RuntimeVersion.Text = "Runtime version";
        Flags = AddTwoBytes("Flags");
        StreamCount = AddTwoBytes("Stream count");

        var peFile = PEFile;

        int count = StreamCount.Value;
        var list = new MetadataStreamHeader[count];

        var embeddedPdbStreams = new List<MetadataStream>();

        var metadataStreams = new List<MetadataStream>();

        for (int i = 0; i < count; i++)
        {
            var stream = Add<MetadataStreamHeader>();
            list[i] = stream;

            int offset = stream.Offset.Value;
            int start = peFile != null ? peFile.ResolveMetadataOffset(offset) : offset;
            int length = stream.Size.Value;
            string streamName = stream.Name.Text;

            MetadataStream metadataStream = null;

            if (streamName == "#~")
            {
                metadataStream = CompressedMetadataTableStream = new CompressedMetadataTableStream();
            }
            else if (streamName == "#-")
            {
                metadataStream = UncompressedMetadataTableStream = new UncompressedMetadataTableStream();
            }
            else if (streamName == "#Strings")
            {
                metadataStream = StringsTableStream = new StringsMetadataStream();
            }
            else if (streamName == "#Blob")
            {
                metadataStream = BlobTableStream = new BlobMetadataStream();
            }
            else if (streamName == "#GUID")
            {
                metadataStream = GuidTableStream = new GuidMetadataStream();
            }
            else if (streamName == "#US")
            {
                metadataStream = UserStringsTableStream = new UserStringsMetadataStream();
            }
            else if (streamName == "#Pdb")
            {
                metadataStream = PdbStream = new PdbStream();
            }
            else if (streamName == "#JTD")
            {
                metadataStream = new MetadataStream();
            }

            if (metadataStream != null)
            {
                metadataStream.Name = streamName;
                metadataStream.Text = $"{streamName} stream";
                metadataStream.Start = start;
                metadataStream.Length = length;
                if (peFile != null)
                {
                    metadataStreams.Add(metadataStream);
                }
                else
                {
                    embeddedPdbStreams.Add(metadataStream);
                }
            }
        }

        metadataStreams.Reverse();
        foreach (var metadataStream in metadataStreams)
        {
            peFile.TextSection.Add(metadataStream);
        }

        for (int i = 0; i < embeddedPdbStreams.Count; i++)
        {
            Add(embeddedPdbStreams[i]);
        }

        StreamHeaders = list;

        if (CompressedMetadataTableStream != null)
        {
            var guidStream = GuidTableStream;
            var blobStream = BlobTableStream;

            CompressedMetadataTableStream.ComputeTableInformations();

            var customDebugInfoTable = CompressedMetadataTableStream.Tables.FirstOrDefault(t => t.Name == Table.CustomDebugInformation);
            if (customDebugInfoTable != null && guidStream != null && blobStream != null)
            {
                foreach (var row in customDebugInfoTable.Children.OfType<CustomDebugInformationTableRow>())
                {
                    int guidHandle = row.GuidHandle.ReadInt16OrInt32();
                    int blobHandle = row.BlobHandle.ReadInt16OrInt32();
                    var customText = guidStream.GetCustomText(guidHandle);
                    if (customText.Contains("CompilationOptions") ||
                        customText.Contains("SourceLink"))
                    {
                        var blob = blobStream.GetBlob(blobHandle);
                        var bytes = blob.Bytes;
                        int start = bytes.Start;
                        int length = bytes.Length;
                        blob.Children.Remove(bytes);
                        var newNode = customText switch
                        {
                            "CompilationOptions" => new CompilationOptions { Length = length },
                            "SourceLink" => new Utf8String { Length = length },
                            _ => bytes
                        };
                        blob.Bytes = blob.Add(newNode);
                        blob.Text = customText;

                        row.Text = customText;
                    }
                }
            }

            CompressedMetadataTableStream.AddRemainingPadding();
        }
    }

    public PEFile PEFile => FindAncestor<PEFile>();

    public FourBytes BSJB { get; set; }
    public TwoBytes MajorVersion { get; set; }
    public TwoBytes MinorVersion { get; set; }
    public FourBytes Reserved { get; set; }
    public TwoBytes Flags { get; set; }
    public TwoBytes StreamCount { get; set; }
    public ZeroTerminatedStringLengthPrefix32 RuntimeVersion { get; set; }
    public IReadOnlyList<MetadataStreamHeader> StreamHeaders { get; set; }

    public CompressedMetadataTableStream CompressedMetadataTableStream { get; set; }
    public UncompressedMetadataTableStream UncompressedMetadataTableStream { get; set; }
    public StringsMetadataStream StringsTableStream { get; set; }
    public GuidMetadataStream GuidTableStream { get; set; }
    public BlobMetadataStream BlobTableStream { get; set; }
    public UserStringsMetadataStream UserStringsTableStream { get; set; }
    public PdbStream PdbStream { get; set; }
    public EmbeddedPdb EmbeddedPdb { get; set; }
}

public class CompilationOptions : Node
{
    public override void Parse()
    {
        while (LastChildEnd < End)
        {
            var keyValue = Add<Utf8KeyValue>();
            keyValue.Text = $"{keyValue.Key.Text} = {keyValue.Value.Text}";
        }
    }
}

public class Utf8KeyValue : Node
{
    public override void Parse()
    {
        Key = Add<ZeroTerminatedString>("Key");
        Value = Add<ZeroTerminatedString>("Value");
    }

    public ZeroTerminatedString Key { get; set; }
    public ZeroTerminatedString Value { get; set; }
}

public class MetadataStream : Node
{
    public string Name { get; set; }
}

public struct TableInfo
{
    public int RowSize;
    public int RowCount;
}

public class StringsMetadataStream : MetadataStream
{
    public override void Parse()
    {
        while (LastChildEnd < End)
        {
            var remainingSpan = new Span(LastChildEnd, End - LastChildEnd);
            if (Buffer.IsZeroFilled(remainingSpan))
            {
                AddRemainingPadding();
                break;
            }

            Add<ZeroTerminatedString>();
        }
    }

    public string FindString(int nameOffset)
    {
        var start = Start + nameOffset;
        var str = new ZeroTerminatedString { Start = start, Buffer = Buffer };
        str.Parse();
        return str.Text;
    }
}

public class UserStringsMetadataStream : MetadataStream
{
    public override void Parse()
    {
        Zero = AddOneByte("Zero byte");
        while (LastChildEnd < End)
        {
            Padding = AddRemainingPadding();
            if (Padding != null)
            {
                break;
            }

            Add<UserString>();
        }
    }

    public OneByte Zero { get; set; }
    public Padding Padding { get; set; }
}

public class UserString : Node
{
    public override void Parse()
    {
        CompressedByteLength = Add<CompressedInteger>("Byte length");
        int length = CompressedByteLength.Value;
        if (length > 0)
        {
            length -= 1;
            var bytes = Buffer.ReadBytes(CompressedByteLength.End, length);
            
            var str = Encoding.Unicode.GetString(bytes);
            Text = str;
            Utf16Chars = AddBytes(length, "Utf-16 chars");

            Add<OneByte>("Final byte");
        }
    }

    public CompressedInteger CompressedByteLength { get; set; }
    public Node Utf16Chars { get; set; }
    public OneByte FinalByte { get; set; }
}

public class CompressedInteger : BytesNode
{
    public override void Parse()
    {
        byte b = Buffer.ReadByte(Start);
        int value = 0;
        if ((b & 0x80) == 0)
        {
            value = b;
            CompressedByteLength = AddOneByte($"{b}");
        }
        else
        {
            if ((b & 0x40) == 0)
            {
                var b2 = Buffer.ReadByte(Start + 1);
                value = (b & ~0x80) << 8 | b2;
                CompressedByteLength = AddTwoBytes($"{value}");
            }
            else
            {
                var b1 = b;
                var b2 = Buffer.ReadByte(Start + 1);
                var b3 = Buffer.ReadByte(Start + 2);
                var b4 = Buffer.ReadByte(Start + 3);
                value = (b & ~0xc0) << 24 | b2 << 16 | b3 << 8 | b4;
                CompressedByteLength = AddFourBytes($"{value}");
            }
        }

        Value = value;
    }

    public BytesNode CompressedByteLength { get; set; }
    public int Value { get; set; }

    public override int ReadInt16OrInt32()
    {
        return Value;
    }
}

public class GuidMetadataStream : MetadataStream
{
    public List<string> guidText = new();

    public override void Parse()
    {
        int index = 1;
        while (LastChildEnd < End)
        {
            var node = Add<SixteenBytes>();
            int offset = node.Start - Start;
            var guid = node.ReadGuid();
            string customText = null;
            node.Text = guid.ToString("D").ToUpperInvariant();
            if (KnownGuids.TryGetValue(guid, out var text))
            {
                customText = $"{text}";
                node.Text = customText;
            }

            guidText.Add(customText);

            index++;
        }
    }

    public string GetCustomText(int indexOneBased)
    {
        return guidText[indexOneBased - 1];
    }

    public static Dictionary<Guid, string> KnownGuids = new()
    {
        [Guid.Parse("54fd2ac5-e925-401a-9c2a-f94f171072f8")] = "AsyncMethodSteppingInformationBlob",
        [Guid.Parse("7e4d4708-096e-4c5c-aeda-cb10ba6a740d")] = "CompilationMetadataReferences",
        [Guid.Parse("b5feec05-8cd0-4a83-96da-466284bb4bd8")] = "CompilationOptions",
        [Guid.Parse("58b2eab6-209f-4e4e-a22c-b2d0f910c782")] = "DefaultNamespace",
        [Guid.Parse("83c563c4-b4f3-47d5-b824-ba5441477ea8")] = "DynamicLocalVariables",
        [Guid.Parse("0e8a571b-6926-466e-b4ad-8ab04611f5fe")] = "EmbeddedSource",
        [Guid.Parse("a643004c-0240-496f-a783-30d64f4979de")] = "EncLambdaAndClosureMap",
        [Guid.Parse("755f52a8-91c5-45be-b4b8-209571e552bd")] = "EncLocalSlotMap",
        [Guid.Parse("8b78cd68-2ede-420b-980b-e15884b8aaa3")] = "EncStateMachineStateMap",
        [Guid.Parse("9d40ace1-c703-4d0e-bf41-7243060a8fb5")] = "PrimaryConstructorInformationBlob",
        [Guid.Parse("cc110556-a091-4d38-9fec-25ab9a351a6a")] = "SourceLink",
        [Guid.Parse("6da9a61e-f8c7-4874-be62-68bc5630df71")] = "StateMachineHoistedLocalScopes",
        [Guid.Parse("ed9fdf71-8879-4747-8ed3-fe5ede3ce710")] = "TupleElementNames",
        [Guid.Parse("932e74bc-dba9-4478-8d46-0f32a7bab3d3")] = "TypeDefinitionDocuments"
    };
}

public class BlobMetadataStream : MetadataStream
{
    public override void Parse()
    {
        while (LastChildEnd < End)
        {
            Padding = AddRemainingPadding();
            if (Padding != null)
            {
                break;
            }

            Add<Blob>();
        }
    }

    public Blob GetBlob(int offset)
    {
        return Children.Where(b => b.Start - Start == offset).FirstOrDefault() as Blob;
    }

    public Padding Padding { get; set; }
}

public class Blob : BytesNode
{
    public override void Parse()
    {
        CompressedByteLength = Add<CompressedInteger>("Length");
        int length = CompressedByteLength.Value;
        if (length > 0)
        {
            Bytes = AddBytes(length, "Bytes");
        }
    }

    public CompressedInteger CompressedByteLength { get; set; }
    public Node Bytes { get; set; }
}

public class CompressedMetadataTableStream : MetadataStream
{
    public const int MaxTables = 58;

    public TableInfo[] TableInfos = new TableInfo[MaxTables];

    public override void Parse()
    {
        ReservedZero = AddFourBytes("Reserved zero");
        MajorVersion = AddOneByte("Major version");
        MinorVersion = AddOneByte("Minor version");
        HeapSizes = AddOneByte("Heap sizes bit vector");
        ReservedByte = AddOneByte("Reserved byte");
        Valid = AddEightBytes("Valid bit vector");
        Sorted = AddEightBytes("Sorted bit vector");

        TableLengths = Add<Sequence>("Table lengths");

        ulong valid = Valid.ReadUInt64();

        for (int i = 0; i < MaxTables; i++)
        {
            if ((valid & (1UL << i)) == 0)
            {
                continue;
            }

            var tableLength = TableLengths.AddFourBytes();
            TableInfos[i].RowCount = tableLength.Value;
            tableLength.Text = $"{(Table)i} table length: {tableLength.Value}";
        }

        PEFile = FindAncestor<PEFile>();
    }

    public FourBytes ReservedZero { get; set; }
    public OneByte MajorVersion { get; set; }
    public OneByte MinorVersion { get; set; }
    public OneByte HeapSizes { get; set; }
    public OneByte ReservedByte { get; set; }
    public EightBytes Valid { get; set; }
    public EightBytes Sorted { get; set; }
    public Sequence TableLengths { get; set; }

    public IReadOnlyList<MetadataTable> Tables { get; set; }

    public Metadata Metadata => Parent switch
    {
        Metadata m => m,
        PEFile peFile => peFile.Metadata,
        Node textSection => ((PEFile)textSection.Parent).Metadata,
        _ => null
    };

    public PEFile PEFile { get; set; }

    int GetTableIndexSize(Table table) => TableInfos[(int)table].RowCount < 65536 ? 2 : 4;

    readonly int[] coded_index_sizes = new int[14];

    int GetCodedIndexSize(CodedIndex codedIndex)
    {
        var index = (int)codedIndex;
        var size = coded_index_sizes[index];
        if (size != 0)
        {
            return size;
        }

        return coded_index_sizes[index] = codedIndex.GetSize(t => TableInfos[(int)t].RowCount);
    }

    public void ComputeTableInformations()
    {
        int heapsizes = HeapSizes.Value;
        int stridx_size = 2;
        int guididx_size = 2;
        int blobidx_size = 2;

        if (Metadata.StringsTableStream != null && (heapsizes & 1) != 0)
        {
            stridx_size = 4;
        }

        if (Metadata.GuidTableStream != null && (heapsizes & 2) != 0)
        {
            guididx_size = 4;
        }

        if (Metadata.BlobTableStream != null && (heapsizes & 4) != 0)
        {
            blobidx_size = 4;
        }

        ulong valid = Valid.ReadUInt64();

        var tables = new List<MetadataTable>();

        for (int i = 0; i < MaxTables; i++)
        {
            if ((valid & (1UL << i)) == 0)
            {
                continue;
            }

            int size;
            Table tableKind = (Table)i;
            switch (tableKind)
            {
                case Table.Module:
                    size = 2    // Generation
                        + stridx_size   // Name
                        + (guididx_size * 3);   // Mvid, EncId, EncBaseId
                    break;
                case Table.TypeRef:
                    size = GetCodedIndexSize(CodedIndex.ResolutionScope)    // ResolutionScope
                        + (stridx_size * 2);    // Name, Namespace
                    break;
                case Table.TypeDef:
                    size = 4    // Flags
                        + (stridx_size * 2) // Name, Namespace
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef)    // BaseType
                        + GetTableIndexSize(Table.Field)    // FieldList
                        + GetTableIndexSize(Table.Method);  // MethodList
                    break;
                case Table.FieldPtr:
                    size = GetTableIndexSize(Table.Field);  // Field
                    break;
                case Table.Field:
                    size = 2    // Flags
                        + stridx_size   // Name
                        + blobidx_size; // Signature
                    break;
                case Table.MethodPtr:
                    size = GetTableIndexSize(Table.Method); // Method
                    break;
                case Table.Method:
                    size = 8    // Rva 4, ImplFlags 2, Flags 2
                        + stridx_size   // Name
                        + blobidx_size  // Signature
                        + GetTableIndexSize(Table.Param); // ParamList
                    break;
                case Table.ParamPtr:
                    size = GetTableIndexSize(Table.Param); // Param
                    break;
                case Table.Param:
                    size = 4    // Flags 2, Sequence 2
                        + stridx_size;  // Name
                    break;
                case Table.InterfaceImpl:
                    size = GetTableIndexSize(Table.TypeDef) // Class
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef);   // Interface
                    break;
                case Table.MemberRef:
                    size = GetCodedIndexSize(CodedIndex.MemberRefParent)    // Class
                        + stridx_size   // Name
                        + blobidx_size; // Signature
                    break;
                case Table.Constant:
                    size = 2    // Type
                        + GetCodedIndexSize(CodedIndex.HasConstant) // Parent
                        + blobidx_size; // Value
                    break;
                case Table.CustomAttribute:
                    size = GetCodedIndexSize(CodedIndex.HasCustomAttribute) // Parent
                        + GetCodedIndexSize(CodedIndex.CustomAttributeType) // Type
                        + blobidx_size; // Value
                    break;
                case Table.FieldMarshal:
                    size = GetCodedIndexSize(CodedIndex.HasFieldMarshal)    // Parent
                        + blobidx_size; // NativeType
                    break;
                case Table.DeclSecurity:
                    size = 2    // Action
                        + GetCodedIndexSize(CodedIndex.HasDeclSecurity) // Parent
                        + blobidx_size; // PermissionSet
                    break;
                case Table.ClassLayout:
                    size = 6    // PackingSize 2, ClassSize 4
                        + GetTableIndexSize(Table.TypeDef); // Parent
                    break;
                case Table.FieldLayout:
                    size = 4    // Offset
                        + GetTableIndexSize(Table.Field);   // Field
                    break;
                case Table.StandAloneSig:
                    size = blobidx_size;    // Signature
                    break;
                case Table.EventMap:
                    size = GetTableIndexSize(Table.TypeDef) // Parent
                        + GetTableIndexSize(Table.Event);   // EventList
                    break;
                case Table.EventPtr:
                    size = GetTableIndexSize(Table.Event);  // Event
                    break;
                case Table.Event:
                    size = 2    // Flags
                        + stridx_size // Name
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef);   // EventType
                    break;
                case Table.PropertyMap:
                    size = GetTableIndexSize(Table.TypeDef) // Parent
                        + GetTableIndexSize(Table.Property);    // PropertyList
                    break;
                case Table.PropertyPtr:
                    size = GetTableIndexSize(Table.Property);   // Property
                    break;
                case Table.Property:
                    size = 2    // Flags
                        + stridx_size   // Name
                        + blobidx_size; // Type
                    break;
                case Table.MethodSemantics:
                    size = 2    // Semantics
                        + GetTableIndexSize(Table.Method)   // Method
                        + GetCodedIndexSize(CodedIndex.HasSemantics);   // Association
                    break;
                case Table.MethodImpl:
                    size = GetTableIndexSize(Table.TypeDef) // Class
                        + GetCodedIndexSize(CodedIndex.MethodDefOrRef)  // MethodBody
                        + GetCodedIndexSize(CodedIndex.MethodDefOrRef); // MethodDeclaration
                    break;
                case Table.ModuleRef:
                    size = stridx_size; // Name
                    break;
                case Table.TypeSpec:
                    size = blobidx_size;    // Signature
                    break;
                case Table.ImplMap:
                    size = 2    // MappingFlags
                        + GetCodedIndexSize(CodedIndex.MemberForwarded) // MemberForwarded
                        + stridx_size   // ImportName
                        + GetTableIndexSize(Table.ModuleRef);   // ImportScope
                    break;
                case Table.FieldRVA:
                    size = 4    // RVA
                        + GetTableIndexSize(Table.Field);   // Field
                    break;
                case Table.EncLog:
                    size = 8;
                    break;
                case Table.EncMap:
                    size = 4;
                    break;
                case Table.Assembly:
                    size = 16 // HashAlgId 4, Version 4 * 2, Flags 4
                        + blobidx_size  // PublicKey
                        + (stridx_size * 2);    // Name, Culture
                    break;
                case Table.AssemblyProcessor:
                    size = 4;   // Processor
                    break;
                case Table.AssemblyOS:
                    size = 12;  // Platform 4, Version 2 * 4
                    break;
                case Table.AssemblyRef:
                    size = 12   // Version 2 * 4 + Flags 4
                        + (blobidx_size * 2)    // PublicKeyOrToken, HashValue
                        + (stridx_size * 2);    // Name, Culture
                    break;
                case Table.AssemblyRefProcessor:
                    size = 4    // Processor
                        + GetTableIndexSize(Table.AssemblyRef); // AssemblyRef
                    break;
                case Table.AssemblyRefOS:
                    size = 12   // Platform 4, Version 2 * 4
                        + GetTableIndexSize(Table.AssemblyRef); // AssemblyRef
                    break;
                case Table.File:
                    size = 4    // Flags
                        + stridx_size   // Name
                        + blobidx_size; // HashValue
                    break;
                case Table.ExportedType:
                    size = 8    // Flags 4, TypeDefId 4
                        + (stridx_size * 2) // Name, Namespace
                        + GetCodedIndexSize(CodedIndex.Implementation); // Implementation
                    break;
                case Table.ManifestResource:
                    size = 8    // Offset, Flags
                        + stridx_size   // Name
                        + GetCodedIndexSize(CodedIndex.Implementation); // Implementation
                    break;
                case Table.NestedClass:
                    size = GetTableIndexSize(Table.TypeDef) // NestedClass
                        + GetTableIndexSize(Table.TypeDef); // EnclosingClass
                    break;
                case Table.GenericParam:
                    size = 4    // Number, Flags
                        + GetCodedIndexSize(CodedIndex.TypeOrMethodDef) // Owner
                        + stridx_size;  // Name
                    break;
                case Table.MethodSpec:
                    size = GetCodedIndexSize(CodedIndex.MethodDefOrRef) // Method
                        + blobidx_size; // Instantiation
                    break;
                case Table.GenericParamConstraint:
                    size = GetTableIndexSize(Table.GenericParam)    // Owner
                        + GetCodedIndexSize(CodedIndex.TypeDefOrRef);   // Constraint
                    break;
                case Table.Document:
                    size = blobidx_size // Name
                        + guididx_size  // HashAlgorithm
                        + blobidx_size  // Hash
                        + guididx_size; // Language
                    break;
                case Table.MethodDebugInformation:
                    size = GetTableIndexSize(Table.Document)  // Document
                        + blobidx_size; // SequencePoints
                    break;
                case Table.LocalScope:
                    size = GetTableIndexSize(Table.Method)  // Method
                        + GetTableIndexSize(Table.ImportScope)  // ImportScope
                        + GetTableIndexSize(Table.LocalVariable)    // VariableList
                        + GetTableIndexSize(Table.LocalConstant)    // ConstantList
                        + 4 * 2;    // StartOffset, Length
                    break;
                case Table.LocalVariable:
                    size = 2    // Attributes
                        + 2     // Index
                        + stridx_size;  // Name
                    break;
                case Table.LocalConstant:
                    size = stridx_size  // Name
                        + blobidx_size; // Signature
                    break;
                case Table.ImportScope:
                    size = GetTableIndexSize(Table.ImportScope) // Parent
                        + blobidx_size;
                    break;
                case Table.StateMachineMethod:
                    size = GetTableIndexSize(Table.Method) // MoveNextMethod
                        + GetTableIndexSize(Table.Method);  // KickOffMethod
                    break;
                case Table.CustomDebugInformation:
                    size = GetCodedIndexSize(CodedIndex.HasCustomDebugInformation) // Parent
                        + guididx_size  // Kind
                        + blobidx_size; // Value
                    break;
                default:
                    throw new NotSupportedException();
            }

            TableInfos[i].RowSize = size;

            var table = Add<MetadataTable>();
            table.Name = tableKind;
            table.Text = $"{tableKind} table";
            for (int row = 0; row < TableInfos[i].RowCount; row++)
            {
                TableRow tableRow = null;
                if (tableKind == Table.TypeDef)
                {
                    tableRow = new TypeDefTableRow
                    {
                        Length = size,
                        Name = new BytesNode
                        {
                            Length = stridx_size,
                            Text = "Name"
                        },
                        Namespace = new BytesNode
                        {
                            Length = stridx_size,
                            Text = "Namespace"
                        },
                        Extends = new BytesNode
                        {
                            Length = GetCodedIndexSize(CodedIndex.TypeDefOrRef),
                            Text = "Extends"
                        },
                        FieldList = new BytesNode
                        {
                            Length = GetTableIndexSize(Table.Field),
                            Text = "FieldList"
                        },
                        MethodList = new BytesNode
                        {
                            Length = GetTableIndexSize(Table.Method),
                            Text = "MethodList"
                        }
                    };
                }
                else if (tableKind == Table.Method)
                {
                    BytesNode nameNode = stridx_size == 2 ? new TwoBytes() : new FourBytes();
                    nameNode.Text = "Name";
                    tableRow = new MethodTableRow
                    {
                        Length = size,
                        Name = nameNode,
                        Signature = new BytesNode
                        {
                            Length = blobidx_size,
                            Text = "Signature"
                        },
                        ParamList = new Node
                        {
                            Length = GetTableIndexSize(Table.Param),
                            Text = "ParamList"
                        }
                    };
                }
                else if (tableKind == Table.Field)
                {
                    tableRow = new FieldTableRow
                    {
                        Length = size,
                        Name = new BytesNode
                        {
                            Length = stridx_size,
                            Text = "Name"
                        },
                        Signature = new BytesNode
                        {
                            Length = blobidx_size,
                            Text = "Signature"
                        }
                    };
                }
                else if (tableKind == Table.FieldRVA)
                {
                    tableRow = new FieldRVATableRow
                    {
                        Length = size,
                        FieldIndex = new BytesNode
                        {
                            Length = GetTableIndexSize(Table.Field),
                            Text = "Field index"
                        }
                    };
                }
                else if (tableKind == Table.CustomDebugInformation)
                {
                    tableRow = new CustomDebugInformationTableRow
                    {
                        Length = size,
                        ParentHandle = new Node
                        {
                            Length = GetCodedIndexSize(CodedIndex.HasCustomDebugInformation),
                            Text = "Parent handle"
                        },
                        GuidHandle = new BytesNode
                        {
                            Length = guididx_size,
                            Text = "Guid handle"
                        },
                        BlobHandle = new BytesNode
                        {
                            Length = blobidx_size,
                            Text = "Blob handle"
                        }
                    };
                }
                else if (tableKind == Table.ManifestResource)
                {
                    BytesNode nameNode = stridx_size == 2 ? new TwoBytes() : new FourBytes();
                    nameNode.Text = "Name";
                    var implementationNode = new BytesNode
                    {
                        Text = "Implementation",
                        Length = GetCodedIndexSize(CodedIndex.Implementation)
                    };
                    tableRow = new ManifestResourceTableRow
                    {
                        Length = size,
                        Name = nameNode,
                        Implementation = implementationNode
                    };
                }
                else
                {
                    tableRow = new TableRow
                    {
                        Length = size
                    };
                }

                table.Add(tableRow);
                if (tableRow is MethodTableRow methodTableRow)
                {
                    int nameOffset = methodTableRow.Name.ReadInt16OrInt32();
                    var zeroTerminatedString = Metadata.StringsTableStream.FindString(nameOffset);
                    methodTableRow.Text = $"Method row: {zeroTerminatedString}";
                    FindMethod(methodTableRow.RVA.Value, zeroTerminatedString);
                }
                else if (tableRow is ManifestResourceTableRow manifestResourceTableRow)
                {
                    FindManagedResource(manifestResourceTableRow);
                }
            }

            tables.Add(table);
        }

        Tables = tables;

        FindMappedFields();
    }

    private MetadataTable GetTable(Table kind) => Tables.FirstOrDefault(t => t.Name == kind);

    private void FindMappedFields()
    {
        var peFile = PEFile;
        var blobTableStream = Metadata.BlobTableStream;
        var typeDefTable = GetTable(Table.TypeDef);
        var fieldRvaTable = GetTable(Table.FieldRVA);
        var fieldTable = GetTable(Table.Field);

        if (fieldRvaTable == null)
        {
            return;
        }

        foreach (var fieldRvaRow in fieldRvaTable.Children.OfType<FieldRVATableRow>())
        {
            var mappedFieldDataSize = 8;

            var rva = fieldRvaRow.RVA.Value;
            var fieldIndex = fieldRvaRow.FieldIndex.ReadUInt16OrUInt32();
            var fieldRow = (FieldTableRow)fieldTable.Children[(int)fieldIndex - 1];
            var signatureBlob = fieldRow.Signature.ReadUInt16OrUInt32();
            var fieldName = fieldRow.Name.ReadUInt16OrUInt32();
            var fieldNameString = Metadata.StringsTableStream.FindString((int)fieldName);
            var blob = blobTableStream.GetBlob((int)signatureBlob);
            var bytes = blob.Bytes;
            if (bytes.Length == 4)
            {
                bytes.AddOneByte("Kind");
                bytes.AddOneByte("ClassOrStruct");
                var compressedInteger = bytes.Add<CompressedInteger>("TypeIndex");
                var typeCodedIndex = compressedInteger.Value;
                var typeIndex = typeCodedIndex >> 2;
                var typeDefRow = typeDefTable.Children[typeIndex - 1] as TypeDefTableRow;
                var nameOffset = typeDefRow.Name.ReadUInt16OrUInt32();
                var zeroTerminatedString = Metadata.StringsTableStream.FindString((int)nameOffset);
                var equals = zeroTerminatedString.IndexOf('=');
                var sizeString = zeroTerminatedString.Substring(equals + 1);
                if (int.TryParse(sizeString, out int size))
                {
                    mappedFieldDataSize = size;
                }
            }

            var offset = peFile.ResolveVirtualAddress(rva);

            var mappedFieldData = new MappedFieldData
            {
                Start = offset,
                Length = mappedFieldDataSize,
                Text = $"Mapped field {fieldNameString}"
            };
            PEFile.Add(mappedFieldData);
        }
    }

    private void FindManagedResource(ManifestResourceTableRow manifestResourceTableRow)
    {
        int offset = manifestResourceTableRow.Offset.Value;
        int flags = manifestResourceTableRow.Flags.Value;
        uint nameOffset = manifestResourceTableRow.Name.ReadUInt16OrUInt32();
        var zeroTerminatedString = Metadata.StringsTableStream.FindString((int)nameOffset);
        manifestResourceTableRow.Text = $"{offset} {zeroTerminatedString}";
        uint implementation = manifestResourceTableRow.Implementation.ReadUInt16OrUInt32();
        uint rid = implementation >> 2;
        switch (implementation & 3)
        {
            case 0: // File
                break;
            case 1: // AssemblyRef
                break;
            default: // ExportedType
                break;
        }

        if (rid == 0)
        {
            var peFile = PEFile;
            var resourceRVA = PEFile.CLIHeader.Resources.RVA.Value;
            var resourceOffset = peFile.ResolveVirtualAddress(resourceRVA + offset);
            var managedResource = new ManagedResource
            {
                Start = resourceOffset,
                Text = zeroTerminatedString
            };
            peFile.Add(managedResource);
        }
    }

    private void FindMethod(int rva, string text)
    {
        if (rva == 0)
        {
            return;
        }

        var peFile = PEFile;
        var offset = peFile.ResolveVirtualAddress(rva);

        byte headerByte = peFile.Buffer.ReadByte(offset);
        byte twoBits = (byte)(headerByte & 3);

        Node method;
        if (twoBits == 2)
        {
            method = ReadTinyMethod(headerByte, offset);
        }
        else
        {
            method = ReadFatMethod(headerByte, offset);
        }

        var codeSize = method switch
        {
            FatMethod f => f.CodeSize.Value,
            TinyMethod t => t.CodeSize,
            _ => 0
        };
        method.Text = $"{method.Text}: {text}";
    }

    private FatMethod ReadFatMethod(byte header, int offset)
    {
        var fatMethod = new FatMethod
        {
            Start = offset,
        };
        PEFile.Add(fatMethod);
        return fatMethod;
    }

    private TinyMethod ReadTinyMethod(byte header, int offset)
    {
        int codeSize = header >> 2;
        var tinyMethod = new TinyMethod
        {
            Start = offset,
            CodeSize = codeSize
        };
        PEFile.Add(tinyMethod);
        return tinyMethod;
    }
}

public class UncompressedMetadataTableStream : MetadataStream
{
}

public class TableRow : Node
{
}

public class TypeDefTableRow : TableRow
{
    public TypeDefTableRow()
    {
        Text = "TypeDef table row";
    }

    public override void Parse()
    {
        Flags = AddFourBytes("Flags");
        Add(Name);
        Add(Namespace);
        Add(Extends);
        Add(FieldList);
        Add(MethodList);
    }

    public FourBytes Flags { get; set; }
    public BytesNode Name { get; set; }
    public BytesNode Namespace { get; set; }
    public BytesNode Extends { get; set; }
    public BytesNode FieldList { get; set; }
    public BytesNode MethodList { get; set; }
}

public class MethodTableRow : TableRow
{
    public MethodTableRow()
    {
        Text = "Method table row";
    }

    public override void Parse()
    {
        RVA = AddFourBytes("RVA");
        ImplFlags = AddTwoBytes("Impl flags");
        Flags = AddTwoBytes("Flags");
        Add(Name);
        Add(Signature);
        Add(ParamList);
    }

    public FourBytes RVA { get; set; }
    public TwoBytes ImplFlags { get; set; }
    public TwoBytes Flags { get; set; }
    public BytesNode Name { get; set; }
    public BytesNode Signature { get; set; }
    public Node ParamList { get; set; }
}

public class FieldTableRow : TableRow
{
    public FieldTableRow()
    {
        Text = "Field table row";
    }

    public override void Parse()
    {
        Flags = AddTwoBytes("Flags");
        Add(Name);
        Add(Signature);
    }

    public TwoBytes Flags { get; set; }
    public BytesNode Name { get; set; }
    public BytesNode Signature { get; set; }
}

public class FieldRVATableRow : TableRow
{
    public FieldRVATableRow()
    {
        Text = "Field RVA table row";
    }

    public override void Parse()
    {
        RVA = AddFourBytes("RVA");
        Add(FieldIndex);
    }

    public FourBytes RVA { get; set; }
    public BytesNode FieldIndex { get; set; }
}

public class CustomDebugInformationTableRow : TableRow
{
    public CustomDebugInformationTableRow()
    {
        Text = "Custom Debug Information table row";
    }

    public override void Parse()
    {
        Add(ParentHandle);
        Add(GuidHandle);
        Add(BlobHandle);
    }

    public Node ParentHandle { get; set; }
    public BytesNode GuidHandle { get; set; }
    public BytesNode BlobHandle { get; set; }
}

public class ManifestResourceTableRow : TableRow
{
    public ManifestResourceTableRow()
    {
        Text = "Manifest resource table row";
    }

    public override void Parse()
    {
        Offset = AddFourBytes("Offset");
        Flags = AddFourBytes("Flags");
        Add(Name);
        Add(Implementation);
    }

    public FourBytes Offset { get; set; }
    public FourBytes Flags { get; set; }
    public BytesNode Name { get; set; }
    public BytesNode Implementation { get; set; }
}

public class MetadataTable : Sequence
{
    public Table Name { get; set; }
}

public class StrongNameSignature : Node
{
    public StrongNameSignature()
    {
        Text = "Strong name signature";
    }
}

public class RuntimeStartupStub : Node
{
    public RuntimeStartupStub()
    {
        Text = "Runtime startup stub";
    }
}

public class ManagedResource : Node
{
    public ManagedResource()
    {
        Text = "Managed Resource";
    }

    public override void Parse()
    {
        Size = AddFourBytes("Size");
        int size = Size.Value;
        Bytes = AddBytes(size, "Bytes");
    }

    public FourBytes Size { get; set; }
    public Node Bytes { get; set; }
}