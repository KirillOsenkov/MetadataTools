// Portions of this file taken from https://github.com/jbevain/cecil
// Copyright (C) Jb Evain

using System;
using System.Collections.Generic;
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
                metadataStream = BlobTableStream = new MetadataStream();
            }
            else if (streamName == "#GUID")
            {
                metadataStream = GuidTableStream = new MetadataStream();
            }
            else if (streamName == "#US")
            {
                metadataStream = UserStringsTableStream = new MetadataStream();
            }
            else if (streamName == "#JTD" || streamName == "#Pdb")
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

        CompressedMetadataTableStream?.AddRemainingPadding();
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
    public MetadataStream GuidTableStream { get; set; }
    public MetadataStream BlobTableStream { get; set; }
    public MetadataStream UserStringsTableStream { get; set; }
    public EmbeddedPdb EmbeddedPdb { get; set; }
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

        TableLengths = Add<Sequence>();
        TableLengths.Text = "Table lengths";

        ulong valid = Valid.ReadUInt64();

        for (int i = 0; i < MaxTables; i++)
        {
            if ((valid & (1UL << i)) == 0)
            {
                continue;
            }

            var tableLength = TableLengths.AddFourBytes("Table length");

            TableInfos[i].RowCount = tableLength.Value;
        }

        PEFile = FindAncestor<PEFile>();

        ComputeTableInformations();
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

    void ComputeTableInformations()
    {
        int heapsizes = HeapSizes.Value;
        int stridx_size = 2;
        int guididx_size = 2;
        int blobidx_size = 2;

        if (Metadata.StringsTableStream != null && (heapsizes & 1) == 1)
        {
            stridx_size = 4;
        }

        if (Metadata.GuidTableStream != null && (heapsizes & 2) == 1)
        {
            guididx_size = 4;
        }

        if (Metadata.BlobTableStream != null && (heapsizes & 4) == 1)
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
                if (tableKind == Table.Method)
                {
                    BytesNode nameNode = stridx_size == 2 ? new TwoBytes() : new FourBytes();
                    nameNode.Text = "Name";
                    tableRow = new MethodTableRow
                    {
                        Length = size,
                        Name = nameNode,
                        Signature = new Node
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
                else if (tableKind == Table.FieldRVA)
                {
                    tableRow = new FieldRVATableRow
                    {
                        Length = size,
                        FieldIndex = new Node
                        {
                            Length = GetTableIndexSize(Table.Field),
                            Text = "Field index"
                        }
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
                    var nameBytes = methodTableRow.Name;
                    int nameOffset = nameBytes.ReadInt16OrInt32();
                    var zeroTerminatedString = Metadata.StringsTableStream.FindString(nameOffset);
                    FindMethod(methodTableRow.RVA.Value, zeroTerminatedString);
                }
                else if (tableRow is FieldRVATableRow fieldRVATableRow)
                {
                    FindField(fieldRVATableRow.RVA.Value);
                }
            }

            tables.Add(table);
        }

        Tables = tables;
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
        method.Text = $"{method.Text}: {text} ({codeSize} bytes)";
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

    private void FindField(int rva)
    {
        if (rva == 0)
        {
            return;
        }

        var peFile = PEFile;
        var offset = peFile.ResolveVirtualAddress(rva);

        var mappedFieldData = new MappedFieldData
        {
            Start = offset,
            Length = 8
        };
        PEFile.Add(mappedFieldData);
    }
}

public class UncompressedMetadataTableStream : MetadataStream
{
}

public class TableRow : Node
{
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
    public Node Signature { get; set; }
    public Node ParamList { get; set; }
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
    public Node FieldIndex { get; set; }
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
