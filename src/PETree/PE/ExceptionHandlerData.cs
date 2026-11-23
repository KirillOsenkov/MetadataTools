using System;
using System.Collections.Generic;
using System.Linq;

namespace GuiLabs.FileFormat.PE;

public enum ExceptionHandlerKind
{
    Unknown,
    ScopeTable,
    FuncInfo,
    FuncInfo4,
    GuardStackOnly
}

public partial class PEFile
{
    public ExceptionTable ExceptionTable { get; set; }

    /// <summary>
    /// Unwind infos of managed code. The bytes between one of these and the
    /// next node are GC info.
    /// </summary>
    public List<Node> ManagedUnwindInfos { get; } = new();

    /// <summary>
    /// Parses the language-specific data that follows each unwind info with a
    /// handler: C scope tables, C++ FuncInfo and /GS cookie data. A handler is
    /// identified by its import name, or by the shape of the data of all the
    /// functions that use it.
    /// </summary>
    private void AddExceptionHandlerData(ExceptionTable table)
    {
        if (table == null || table.HandlerUses.Count == 0)
        {
            return;
        }

        var groups = table.HandlerUses.GroupBy(u => u.HandlerRva).ToArray();
        var kinds = new Dictionary<int, ExceptionHandlerKind>();
        var primaryEnds = new Dictionary<HandlerUse, int>();

        foreach (var group in groups)
        {
            var uses = group.ToArray();
            string name = GetImportThunkName(group.Key);
            var kind = name != null ? GetHandlerKind(name, out _) : ProbeHandlerKind(uses);
            kinds[group.Key] = kind;
        }

        // Scope tables and FuncInfo references first, so that the /GS data
        // check below sees which bytes after them are already claimed
        foreach (var group in groups)
        {
            var kind = kinds[group.Key];
            foreach (var use in group)
            {
                int end = AddPrimaryHandlerData(use, kind);
                if (end > 0)
                {
                    primaryEnds[use] = end;
                }
            }
        }

        foreach (var group in groups)
        {
            var kind = kinds[group.Key];
            var uses = group.ToArray();
            string name = GetImportThunkName(group.Key);
            bool hasGuardStackData;
            if (name != null)
            {
                GetHandlerKind(name, out hasGuardStackData);
            }
            else
            {
                hasGuardStackData = ProbeGuardStackData(table, uses, kind, primaryEnds);
            }

            if (!hasGuardStackData)
            {
                continue;
            }

            foreach (var use in uses)
            {
                int offset = kind == ExceptionHandlerKind.GuardStackOnly || kind == ExceptionHandlerKind.Unknown
                    ? use.DataOffset
                    : primaryEnds.TryGetValue(use, out int end) ? end : 0;
                int size = MeasureGuardStackData(offset);
                if (size > 0)
                {
                    TryAdd(new GuardStackHandlerData { Start = offset, Length = size });
                }
            }
        }
    }

    /// <summary>
    /// The import name when the code at an RVA is a jump through an import
    /// address table slot.
    /// </summary>
    public string GetImportThunkName(int rva)
    {
        if (ExportNamesByRva.TryGetValue(rva, out var exportName))
        {
            return exportName;
        }

        int offset = ResolveVirtualAddress(rva);
        if (offset <= 0 || offset + 7 > Buffer.Length || Machine != MachineAmd64)
        {
            return null;
        }

        int instructionLength;
        if (Buffer.ReadByte(offset) == 0xFF && Buffer.ReadByte(offset + 1) == 0x25)
        {
            instructionLength = 6;
        }
        else if (Buffer.ReadByte(offset) == 0x48 && Buffer.ReadByte(offset + 1) == 0xFF && Buffer.ReadByte(offset + 2) == 0x25)
        {
            instructionLength = 7;
        }
        else
        {
            return null;
        }

        int displacement = Buffer.ReadInt32(offset + instructionLength - 4);
        int slotRva = rva + instructionLength + displacement;
        return ImportNamesBySlotRva.TryGetValue(slotRva, out var name) ? name : null;
    }

    private static ExceptionHandlerKind GetHandlerKind(string importName, out bool hasGuardStackData)
    {
        int bang = importName.IndexOf('!');
        string name = (bang >= 0 ? importName.Substring(bang + 1) : importName).TrimStart('_');
        hasGuardStackData = name.StartsWith("GSHandlerCheck");
        switch (name)
        {
            case "C_specific_handler":
            case "C_specific_handler_noexcept":
            case "GSHandlerCheck_SEH":
            case "GSHandlerCheck_SEH_noexcept":
                return ExceptionHandlerKind.ScopeTable;
            case "CxxFrameHandler":
            case "CxxFrameHandler2":
            case "CxxFrameHandler3":
            case "GSHandlerCheck_EH":
                return ExceptionHandlerKind.FuncInfo;
            case "CxxFrameHandler4":
            case "GSHandlerCheck_EH4":
                return ExceptionHandlerKind.FuncInfo4;
            case "GSHandlerCheck":
                return ExceptionHandlerKind.GuardStackOnly;
            default:
                return ExceptionHandlerKind.Unknown;
        }
    }

    private const int MaximumProbes = 64;

    private ExceptionHandlerKind ProbeHandlerKind(HandlerUse[] uses)
    {
        int sampled = 0;
        int scopeTables = 0;
        int funcInfos = 0;
        int funcInfos4 = 0;
        foreach (var use in uses.Take(MaximumProbes))
        {
            if (use.DataOffset + 4 > Buffer.Length)
            {
                continue;
            }

            sampled++;
            int value = Buffer.ReadInt32(use.DataOffset);
            if (IsFuncInfo(value))
            {
                funcInfos++;
            }
            else if (MeasureScopeTable(use.DataOffset) > 0)
            {
                scopeTables++;
            }
            else if (IsFuncInfo4(value))
            {
                funcInfos4++;
            }
        }

        if (sampled == 0)
        {
            return ExceptionHandlerKind.Unknown;
        }

        if (funcInfos * 2 > sampled)
        {
            return ExceptionHandlerKind.FuncInfo;
        }

        if (scopeTables * 2 > sampled)
        {
            return ExceptionHandlerKind.ScopeTable;
        }

        if (funcInfos4 * 2 > sampled)
        {
            return ExceptionHandlerKind.FuncInfo4;
        }

        return ExceptionHandlerKind.Unknown;
    }

    private bool ProbeGuardStackData(ExceptionTable table, HandlerUse[] uses, ExceptionHandlerKind kind, Dictionary<HandlerUse, int> primaryEnds)
    {
        int sampled = 0;
        int votes = 0;
        foreach (var use in uses.Take(MaximumProbes))
        {
            int offset;
            if (kind == ExceptionHandlerKind.Unknown)
            {
                offset = use.DataOffset;
            }
            else if (!primaryEnds.TryGetValue(use, out offset))
            {
                continue;
            }

            sampled++;
            int size = MeasureGuardStackData(offset);
            if (size > 0 && IsRangeAvailable(offset, size) && !table.UnwindOffsets.Contains(offset))
            {
                votes++;
            }
        }

        if (sampled == 0)
        {
            return false;
        }

        // Without a recognized table in front, every use has to agree
        return kind == ExceptionHandlerKind.Unknown
            ? votes == sampled && sampled >= 2
            : votes * 2 > sampled;
    }

    private int MeasureGuardStackData(int offset)
    {
        if (offset <= 0 || offset + 4 > Buffer.Length)
        {
            return 0;
        }

        int value = Buffer.ReadInt32(offset);
        int cookieOffset = value & ~7;
        if (cookieOffset <= 0 || cookieOffset > 0x100000)
        {
            return 0;
        }

        const int HasAlignment = 4;
        if ((value & HasAlignment) == 0)
        {
            return 4;
        }

        if (offset + 12 > Buffer.Length)
        {
            return 0;
        }

        int alignment = Buffer.ReadInt32(offset + 8);
        bool isPowerOfTwo = alignment > 0 && (alignment & (alignment - 1)) == 0;
        return isPowerOfTwo && alignment <= 0x1000 ? 12 : 0;
    }

    private bool IsFuncInfo(int rva)
    {
        int offset = rva > 0 ? ResolveVirtualAddress(rva) : 0;
        if (offset <= 0 || offset + 4 > Buffer.Length)
        {
            return false;
        }

        uint magic = Buffer.ReadUInt32(offset) & 0x1FFFFFFF;
        return magic >= 0x19930520 && magic <= 0x19930522;
    }

    private bool IsFuncInfo4(int rva)
    {
        int offset = rva > 0 ? ResolveVirtualAddress(rva) : 0;
        if (offset <= 0 || IsCodeAddress(rva))
        {
            return false;
        }

        var cursor = new FieldCursor(Buffer, offset);
        var info = CxxFuncInfo4.Read(cursor);
        if (cursor.Failed || (info.Header & 0x80) != 0)
        {
            return false;
        }

        bool IsDataRva(int value) => value > 0 && ResolveVirtualAddress(value) > 0 && !IsCodeAddress(value);

        return IsDataRva(info.IpToStateMapRva) &&
            (info.UnwindMapRva == 0 || IsDataRva(info.UnwindMapRva)) &&
            (info.TryBlockMapRva == 0 || IsDataRva(info.TryBlockMapRva));
    }

    private int MeasureScopeTable(int offset)
    {
        if (offset + 4 > Buffer.Length)
        {
            return 0;
        }

        int count = Buffer.ReadInt32(offset);
        if (count < 1 || count > 4096 || (long)offset + 4 + count * 16L > Buffer.Length)
        {
            return 0;
        }

        for (int i = 0; i < count; i++)
        {
            int record = offset + 4 + i * 16;
            int begin = Buffer.ReadInt32(record);
            int end = Buffer.ReadInt32(record + 4);
            int handler = Buffer.ReadInt32(record + 8);
            int target = Buffer.ReadInt32(record + 12);
            if (begin <= 0 || end < begin || !IsCodeAddress(begin) || !IsCodeAddress(end - (end > begin ? 1 : 0)))
            {
                return 0;
            }

            if (handler != 0 && handler != 1 && !IsCodeAddress(handler))
            {
                return 0;
            }

            if (target != 0 && !IsCodeAddress(target))
            {
                return 0;
            }
        }

        return 4 + count * 16;
    }

    /// <summary>
    /// Adds the scope table or FuncInfo reference of one unwind info and
    /// returns the offset where it ends, or 0.
    /// </summary>
    private int AddPrimaryHandlerData(HandlerUse use, ExceptionHandlerKind kind)
    {
        int offset = use.DataOffset;
        if (kind == ExceptionHandlerKind.ScopeTable)
        {
            int size = MeasureScopeTable(offset);
            if (size == 0)
            {
                return 0;
            }

            TryAdd(new ScopeTable { Start = offset, Length = size });
            return offset + size;
        }

        if (kind == ExceptionHandlerKind.FuncInfo || kind == ExceptionHandlerKind.FuncInfo4)
        {
            if (offset + 4 > Buffer.Length)
            {
                return 0;
            }

            int rva = Buffer.ReadInt32(offset);
            bool isValid = kind == ExceptionHandlerKind.FuncInfo ? IsFuncInfo(rva) : IsFuncInfo4(rva);
            if (!isValid)
            {
                return 0;
            }

            var reference = new FourBytes { Start = offset, Text = $"FuncInfo RVA 0x{rva:X}" };
            if (IsRangeAvailable(offset, 4))
            {
                Add(reference);
                reference.Text = $"FuncInfo RVA 0x{rva:X}";
            }

            if (kind == ExceptionHandlerKind.FuncInfo)
            {
                AddCxxFuncInfo(ResolveVirtualAddress(rva), rva);
            }
            else
            {
                AddCxxFuncInfo4(ResolveVirtualAddress(rva), rva, use.FunctionBegin);
            }

            return offset + 4;
        }

        return 0;
    }

    private readonly HashSet<int> cxxDataOffsets = new();

    private void AddCxxFuncInfo(int offset, int rva)
    {
        if (!cxxDataOffsets.Add(offset))
        {
            return;
        }

        uint magic = Buffer.ReadUInt32(offset) & 0x1FFFFFFF;
        int size = magic == 0x19930520 ? 32 : magic == 0x19930521 ? 36 : 40;
        var fields = new List<(string, int)>
        {
            ("Magic number", 4),
            ("Max state", 4),
            ("Unwind map RVA", 4),
            ("Try block count", 4),
            ("Try block map RVA", 4),
            ("IP map entry count", 4),
            ("IP to state map RVA", 4),
            ("Unwind help offset", 4)
        };
        if (size >= 36)
        {
            fields.Add(("ES type list RVA", 4));
        }

        if (size >= 40)
        {
            fields.Add(("EH flags", 4));
        }

        var funcInfo = new Record { Start = offset, Length = size, Fields = fields.ToArray() };
        if (!TryAdd(funcInfo))
        {
            return;
        }

        funcInfo.Text = $"C++ FuncInfo (0x{rva:X})";

        int maxState = funcInfo.ReadInt32(1);
        int unwindMapRva = funcInfo.ReadInt32(2);
        int tryBlockCount = funcInfo.ReadInt32(3);
        int tryBlockMapRva = funcInfo.ReadInt32(4);
        int ipMapCount = funcInfo.ReadInt32(5);
        int ipMapRva = funcInfo.ReadInt32(6);

        AddCxxTable(unwindMapRva, maxState, "C++ unwind map", "Unwind map entry", [("To state", 4), ("Action RVA", 4)]);
        AddCxxTable(ipMapRva, ipMapCount, "C++ IP to state map", "IP to state entry", [("IP RVA", 4), ("State", 4)]);
        var tryBlocks = AddCxxTable(
            tryBlockMapRva,
            tryBlockCount,
            "C++ try block map",
            "Try block",
            [("Try low", 4), ("Try high", 4), ("Catch high", 4), ("Catch count", 4), ("Handler array RVA", 4)]);
        if (tryBlocks == null)
        {
            return;
        }

        foreach (var tryBlock in tryBlocks.Children.OfType<Record>())
        {
            AddCxxTable(
                tryBlock.ReadInt32(4),
                tryBlock.ReadInt32(3),
                "C++ catch handlers",
                "Handler type",
                [("Adjectives", 4), ("Type descriptor RVA", 4), ("Catch object offset", 4), ("Handler RVA", 4), ("Frame offset", 4)]);
        }
    }

    private RecordTable AddCxxTable(int rva, int count, string text, string recordText, (string, int)[] fields)
    {
        int offset = rva > 0 ? ResolveVirtualAddress(rva) : 0;
        int recordSize = fields.Sum(f => f.Item2);
        if (offset <= 0 || count <= 0 || count > 0x100000 || !cxxDataOffsets.Add(offset))
        {
            return null;
        }

        var table = new RecordTable
        {
            Start = offset,
            Length = count * recordSize,
            Count = count,
            Fields = fields,
            RecordText = recordText
        };
        if (!TryAdd(table))
        {
            return null;
        }

        table.Text = $"{text} ({count})";
        return table;
    }

    private void AddCxxFuncInfo4(int offset, int rva, int functionBegin)
    {
        var info = AddCxxNode<CxxFuncInfo4>(offset, $"C++ FuncInfo4 (0x{rva:X})");
        if (info == null)
        {
            return;
        }

        var data = info.Info;
        if (data.UnwindMapRva != 0)
        {
            AddCxxNode<CxxUnwindMap4>(ResolveVirtualAddress(data.UnwindMapRva), "C++ unwind map (compressed)");
        }

        if (data.TryBlockMapRva != 0)
        {
            var tryBlockMap = AddCxxNode<CxxTryBlockMap4>(ResolveVirtualAddress(data.TryBlockMapRva), "C++ try block map (compressed)");
            if (tryBlockMap != null)
            {
                foreach (int handlerArrayRva in tryBlockMap.HandlerArrayRvas)
                {
                    AddCxxNode<CxxHandlerMap4>(ResolveVirtualAddress(handlerArrayRva), "C++ catch handlers (compressed)");
                }
            }
        }

        if ((data.Header & CxxFuncInfo4.IsSeparated) != 0)
        {
            var separated = AddCxxNode<CxxSeparatedIpToStateMap4>(ResolveVirtualAddress(data.IpToStateMapRva), "C++ separated IP to state map");
            if (separated != null)
            {
                foreach (int mapRva in separated.IpMapRvas)
                {
                    AddCxxNode<CxxIpToStateMap4>(ResolveVirtualAddress(mapRva), "C++ IP to state map (compressed)");
                }
            }
        }
        else
        {
            AddCxxNode<CxxIpToStateMap4>(ResolveVirtualAddress(data.IpToStateMapRva), "C++ IP to state map (compressed)");
        }
    }

    private T AddCxxNode<T>(int offset, string text) where T : CompressedCxxNode, new()
    {
        if (offset <= 0)
        {
            return null;
        }

        if (!cxxDataOffsets.Add(offset))
        {
            return null;
        }

        var node = new T { Start = offset };

        int size = node.Measure(Buffer);
        if (size <= 0)
        {
            return null;
        }

        node.Length = size;
        if (!TryAdd(node))
        {
            return null;
        }

        node.Text = text;
        return node;
    }
}

public class ScopeTable : Node
{
    public override void Parse()
    {
        var count = AddFourBytes("Count");
        for (int i = 0; i < count.Value; i++)
        {
            var record = new Record
            {
                Fields =
                [
                    ("Begin address", 4),
                    ("End address", 4),
                    ("Handler address", 4),
                    ("Jump target", 4)
                ]
            };
            Add(record);
            record.Text = $"Scope 0x{record.ReadInt32(0):X}-0x{record.ReadInt32(1):X}";
        }

        Text = $"C scope table ({count.Value})";
    }
}

public class GuardStackHandlerData : Node
{
    public override void Parse()
    {
        var cookie = AddFourBytes("GS cookie offset and flags");
        cookie.Text = $"GS cookie offset 0x{cookie.Value & ~7:X}, flags {cookie.Value & 7}";
        if (Length >= 12)
        {
            AddFourBytes("Aligned base offset");
            AddFourBytes("Alignment");
        }

        Text = "GS handler data";
    }
}

/// <summary>
/// Base of the nodes of the compressed C++ exception data (__CxxFrameHandler4).
/// A subclass reads its fields in Walk; Measure runs Walk without a target.
/// </summary>
public abstract class CompressedCxxNode : Node
{
    protected abstract void Walk(FieldCursor cursor);

    public int Measure(ByteBuffer buffer)
    {
        var cursor = new FieldCursor(buffer, Start);
        Walk(cursor);
        return cursor.Failed ? 0 : cursor.Position - Start;
    }

    public override void Parse()
    {
        Walk(new FieldCursor(Buffer, Start, this));
    }

    protected const int MaximumEntries = 0x10000;
}

public struct CxxFuncInfo4Data
{
    public byte Header;
    public int UnwindMapRva;
    public int TryBlockMapRva;
    public int IpToStateMapRva;
}

public class CxxFuncInfo4 : CompressedCxxNode
{
    public const int IsCatch = 0x01;
    public const int IsSeparated = 0x02;
    public const int HasBbtFlags = 0x04;
    public const int HasUnwindMap = 0x08;
    public const int HasTryBlockMap = 0x10;

    public CxxFuncInfo4Data Info { get; private set; }

    public static CxxFuncInfo4Data Read(FieldCursor cursor)
    {
        var info = new CxxFuncInfo4Data();
        info.Header = cursor.Byte("Header");
        if ((info.Header & HasBbtFlags) != 0)
        {
            cursor.CompressedUInt32("BBT flags");
        }

        if ((info.Header & HasUnwindMap) != 0)
        {
            info.UnwindMapRva = cursor.Int32("Unwind map RVA");
        }

        if ((info.Header & HasTryBlockMap) != 0)
        {
            info.TryBlockMapRva = cursor.Int32("Try block map RVA");
        }

        info.IpToStateMapRva = cursor.Int32((info.Header & IsSeparated) != 0 ? "Separated IP to state map RVA" : "IP to state map RVA");
        if ((info.Header & IsCatch) != 0)
        {
            cursor.CompressedUInt32("Frame offset");
        }

        return info;
    }

    protected override void Walk(FieldCursor cursor)
    {
        Info = Read(cursor);
    }
}

public class CxxUnwindMap4 : CompressedCxxNode
{
    protected override void Walk(FieldCursor cursor)
    {
        uint count = cursor.CompressedUInt32("Entry count");
        if (count > MaximumEntries)
        {
            cursor.Fail();
            return;
        }

        for (uint i = 0; i < count && !cursor.Failed; i++)
        {
            uint nextOffsetAndType = cursor.CompressedUInt32("Next offset and type");
            uint type = nextOffsetAndType & 3;
            if (type == 1 || type == 2)
            {
                cursor.Int32("Action RVA");
                cursor.CompressedUInt32("Object offset");
            }
            else if (type == 3)
            {
                cursor.Int32("Action RVA");
            }
        }
    }
}

public class CxxTryBlockMap4 : CompressedCxxNode
{
    public List<int> HandlerArrayRvas { get; } = new();

    protected override void Walk(FieldCursor cursor)
    {
        HandlerArrayRvas.Clear();
        uint count = cursor.CompressedUInt32("Try block count");
        if (count > MaximumEntries)
        {
            cursor.Fail();
            return;
        }

        for (uint i = 0; i < count && !cursor.Failed; i++)
        {
            cursor.CompressedUInt32("Try low");
            cursor.CompressedUInt32("Try high");
            cursor.CompressedUInt32("Catch high");
            HandlerArrayRvas.Add(cursor.Int32("Handler array RVA"));
        }
    }
}

public class CxxHandlerMap4 : CompressedCxxNode
{
    protected override void Walk(FieldCursor cursor)
    {
        uint count = cursor.CompressedUInt32("Handler count");
        if (count > MaximumEntries)
        {
            cursor.Fail();
            return;
        }

        for (uint i = 0; i < count && !cursor.Failed; i++)
        {
            byte header = cursor.Byte("Handler header");
            if ((header & 0x01) != 0)
            {
                cursor.CompressedUInt32("Adjectives");
            }

            if ((header & 0x02) != 0)
            {
                cursor.Int32("Type descriptor RVA");
            }

            if ((header & 0x04) != 0)
            {
                cursor.CompressedUInt32("Catch object offset");
            }

            cursor.Int32("Handler RVA");

            bool continuationIsRva = (header & 0x08) != 0;
            int continuationCount = (header >> 4) & 3;
            for (int j = 0; j < continuationCount && j < 2; j++)
            {
                if (continuationIsRva)
                {
                    cursor.Int32("Continuation RVA");
                }
                else
                {
                    cursor.CompressedUInt32("Continuation offset");
                }
            }
        }
    }
}

public class CxxIpToStateMap4 : CompressedCxxNode
{
    protected override void Walk(FieldCursor cursor)
    {
        uint count = cursor.CompressedUInt32("Entry count");
        if (count > MaximumEntries)
        {
            cursor.Fail();
            return;
        }

        for (uint i = 0; i < count && !cursor.Failed; i++)
        {
            cursor.CompressedUInt32("IP delta");
            cursor.CompressedUInt32("State + 1");
        }
    }
}

public class CxxSeparatedIpToStateMap4 : CompressedCxxNode
{
    public List<int> IpMapRvas { get; } = new();

    protected override void Walk(FieldCursor cursor)
    {
        IpMapRvas.Clear();
        uint count = cursor.CompressedUInt32("Segment count");
        if (count > MaximumEntries)
        {
            cursor.Fail();
            return;
        }

        for (uint i = 0; i < count && !cursor.Failed; i++)
        {
            cursor.Int32("Segment start RVA");
            IpMapRvas.Add(cursor.Int32("IP to state map RVA"));
        }
    }
}
