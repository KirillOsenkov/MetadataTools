using System;
using System.Collections.Generic;
using System.Text;

namespace GuiLabs.FileFormat.Wasm;

/// <summary>
/// One assembly that Mono's ahead-of-time compiler compiled into the module.
/// </summary>
public class MonoAotModule
{
    public string Name { get; set; }

    /// <summary>
    /// Address of the MonoAotFileInfo structure in linear memory.
    /// </summary>
    public long InfoAddress { get; set; }

    public int MethodCount { get; set; }
    public int BlobLength { get; set; }

    /// <summary>
    /// Bytes of the per-method info records, which are stored in front of
    /// the method info offsets and not in the blob.
    /// </summary>
    public int MethodInfoLength { get; set; }

    public int MethodInfoCount { get; set; }

    /// <summary>
    /// Number and size of the functions of the module that the method table
    /// and the unbox trampoline table of the assembly list. A function that
    /// several assemblies list is counted for the first one.
    /// </summary>
    public int FunctionCount { get; set; }

    public long CodeLength { get; set; }

    /// <summary>
    /// Bytes in the file for each kind of data of the assembly, such as
    /// "blob" or "method info".
    /// </summary>
    public Dictionary<string, long> DataLengths { get; } = new();

    /// <summary>
    /// The file ranges that are counted in the data lengths.
    /// </summary>
    public List<(int start, int length)> DataRanges { get; } = new();

    /// <summary>
    /// The module version id of the assembly that was compiled, as text.
    /// </summary>
    public string AssemblyGuid { get; set; }

    internal long MethodInfoOffsetsAddress;
    internal long MethodFlagsAddress;
    internal long UnboxTrampolinesAddress;
    internal long UnboxTrampolineIndexesAddress;
    internal uint UnboxTrampolineCount;
    internal uint UnboxTrampolineIndexSize;
    internal uint LlvmGotSlotCount;
}

/// <summary>
/// The data tables of Mono's ahead-of-time compiler. Each compiled assembly
/// has a MonoAotFileInfo structure in the data of the module: pointers to the
/// tables of the assembly, counts and flags. The tables are found through
/// these pointers.
///
/// The optimizer that runs after the linker removes runs of zero bytes from
/// the data and starts a new data segment after each run, so a table can be
/// stored in several pieces. The tables are decoded from an image of linear
/// memory, and every piece that is in the file gets a node.
/// </summary>
public partial class WasmFile
{
    /// <summary>
    /// The only version of the file info layout that is decoded.
    /// </summary>
    private const int MonoAotFileVersion = 187;
    private const int MonoAotFileInfoSize = 424;
    private const int MonoAotPointerCount = 39;

    private static readonly string[] monoAotPointerNames =
    [
        "JIT GOT", "Mono EH frame", "LLVM get_method function", "LLVM get_unbox_tramp function", "LLVM init_aotconst function",
        "JIT code start", "JIT code end", "Method addresses", "LLVM unbox trampoline indexes", "LLVM unbox trampolines",
        "Blob", "Class name table", "Class info offsets", "Method info offsets", "Exception info offsets",
        "Extra method info offsets", "Extra method table", "GOT info offsets", "LLVM GOT info offsets", "Image table",
        "Weak field indexes", "Method flags table", "Memory end", "Assembly GUID", "Runtime version",
        "Specific trampolines", "Static rgctx trampolines", "IMT trampolines", "Gsharedvt argument trampolines", "Function pointer argument trampolines",
        "Unbox arbitrary trampolines", "Globals", "Assembly name", "PLT", "PLT end",
        "Unwind info", "Unbox trampolines", "Unbox trampolines end", "Unbox trampoline addresses"
    ];

    private static readonly string[] monoAotScalarNames =
    [
        "PLT GOT offset base", "PLT GOT info offset base", "GOT size", "LLVM GOT size", "PLT size",
        "Method count", "Extra method count", "Flags", "Optimization flags", "SIMD flags",
        "GC name offset in the blob", "Rgctx fetch trampoline count", "Double alignment", "Long alignment", "Generic trampoline count",
        "Card table shift bits", "Card table mask", "Trampoline page size", "Call table entry size", "Shared GOT entry count",
        "Data file size", "LLVM unbox trampoline count", "LLVM unbox trampoline index size", "Exported method count", "Exported methods"
    ];

    private static readonly (string name, int count)[] monoAotArrayNames =
    [
        ("Table offset in the data file", 12),
        ("Trampoline count", 6),
        ("Trampoline GOT offset base", 6),
        ("Trampoline size", 6),
        ("Trampoline page code offset", 6)
    ];

    private const int PointerLlvmUnboxTrampolineIndexes = 8;
    private const int PointerLlvmUnboxTrampolines = 9;
    private const int PointerBlob = 10;
    private const int PointerClassNameTable = 11;
    private const int PointerClassInfoOffsets = 12;
    private const int PointerMethodInfoOffsets = 13;
    private const int PointerExceptionInfoOffsets = 14;
    private const int PointerExtraMethodInfoOffsets = 15;
    private const int PointerExtraMethodTable = 16;
    private const int PointerGotInfoOffsets = 17;
    private const int PointerLlvmGotInfoOffsets = 18;
    private const int PointerImageTable = 19;
    private const int PointerWeakFieldIndexes = 20;
    private const int PointerMethodFlagsTable = 21;
    private const int PointerAssemblyGuid = 23;
    private const int PointerRuntimeVersion = 24;
    private const int PointerAssemblyName = 32;

    private const int ScalarMethodCount = 5;
    private const int ScalarFlags = 7;
    private const int ScalarGcNameIndex = 10;
    private const int ScalarLlvmUnboxTrampolineCount = 21;
    private const int ScalarLlvmUnboxTrampolineIndexSize = 22;

    /// <summary>
    /// The assemblies that have a MonoAotFileInfo structure in the data.
    /// </summary>
    public List<MonoAotModule> MonoAotModules { get; } = new();

    private byte[] memory;
    private long memoryStart;
    private List<WasmDataSegment> segmentsByAddress;

    /// <summary>
    /// A byte range of linear memory with a label. The nodes of the tree are
    /// made from it for the parts of the range that are in the file.
    /// </summary>
    private sealed class MemoryNode
    {
        public long Address;
        public int Length;
        public string Text;
        public bool IsPadding;
        public MonoAotModule Module;
        public List<MemoryNode> Children;

        public long End => Address + Length;

        public MemoryNode Add(long address, long length, string text, bool isPadding = false)
        {
            if (length <= 0)
            {
                return null;
            }

            var child = new MemoryNode { Address = address, Length = (int)length, Text = text, IsPadding = isPadding };
            Children ??= new List<MemoryNode>();
            Children.Add(child);
            return child;
        }
    }

    /// <summary>
    /// Finds the Mono AOT data and returns the nodes for it by index of the
    /// data segment that the bytes are in.
    /// </summary>
    private Dictionary<int, List<Node>> ReadMonoAotData(long addressLimit)
    {
        var result = new Dictionary<int, List<Node>>();
        if (!BuildMemoryImage())
        {
            return result;
        }

        var nodes = new List<MemoryNode>();
        int first = (int)((4 - (memoryStart & 3)) & 3);
        for (int i = first; i + MonoAotFileInfoSize <= memory.Length; i += 4)
        {
            if (BitConverter.ToUInt32(memory, i) != MonoAotFileVersion || BitConverter.ToUInt32(memory, i + 4) != 0)
            {
                continue;
            }

            long info = memoryStart + i;
            if (IsMonoAotFileInfo(info, addressLimit))
            {
                int count = nodes.Count;
                ReadMonoAotModule(info, nodes);
                var module = MonoAotModules[MonoAotModules.Count - 1];
                for (int n = count; n < nodes.Count; n++)
                {
                    nodes[n].Module = module;
                }

                i += MonoAotFileInfoSize - 4;
            }
        }

        nodes.Sort((left, right) => left.Address.CompareTo(right.Address));
        var additions = new List<MemoryNode>();
        foreach (var module in MonoAotModules)
        {
            int count = additions.Count;
            AddMonoMethodInfos(module, nodes, additions);
            AddMonoMethodTable(module, additions);
            if (MonoAotModules.Count == 1 && FunctionPointerRelocations.Count > 0)
            {
                SetFunctionOwnersFromRelocations(module);
            }
            for (int n = count; n < additions.Count; n++)
            {
                additions[n].Module = module;
            }
        }

        nodes.AddRange(additions);

        // A table whose length was computed wrong could reach into the next one
        nodes.Sort((left, right) => left.Address.CompareTo(right.Address));
        long previousEnd = long.MinValue;
        foreach (var node in nodes)
        {
            if (node.Address < previousEnd)
            {
                continue;
            }

            previousEnd = node.End;
            AddFileNodes(node, result);
        }

        return result;
    }

    private bool BuildMemoryImage()
    {
        const long MaximumImageSize = 1L << 30;

        segmentsByAddress = new List<WasmDataSegment>();
        foreach (var segment in DataSegments)
        {
            if (segment.Address >= 0 && segment.ContentLength > 0)
            {
                segmentsByAddress.Add(segment);
            }
        }

        if (segmentsByAddress.Count == 0)
        {
            return false;
        }

        segmentsByAddress.Sort((left, right) => left.Address.CompareTo(right.Address));
        long previousEnd = long.MinValue;
        foreach (var segment in segmentsByAddress)
        {
            if (segment.Address < previousEnd)
            {
                return false;
            }

            previousEnd = segment.Address + segment.ContentLength;
        }

        memoryStart = segmentsByAddress[0].Address;
        long size = previousEnd - memoryStart;
        if (size > MaximumImageSize)
        {
            return false;
        }

        memory = new byte[size];
        foreach (var segment in segmentsByAddress)
        {
            System.Buffer.BlockCopy(Bytes, segment.ContentStart, memory, (int)(segment.Address - memoryStart), segment.ContentLength);
        }

        return true;
    }

    private bool IsInMemory(long address, long length)
    {
        return address >= memoryStart && length >= 0 && address + length <= memoryStart + memory.Length;
    }

    private byte MemoryByte(long address)
    {
        return IsInMemory(address, 1) ? memory[address - memoryStart] : (byte)0;
    }

    private ushort MemoryUInt16(long address)
    {
        return IsInMemory(address, 2) ? BitConverter.ToUInt16(memory, (int)(address - memoryStart)) : (ushort)0;
    }

    private uint MemoryUInt32(long address)
    {
        return IsInMemory(address, 4) ? BitConverter.ToUInt32(memory, (int)(address - memoryStart)) : 0;
    }

    /// <summary>
    /// A zero-terminated string of printable ASCII characters, or null.
    /// </summary>
    private string ReadMemoryString(long address, int maximumLength, bool allowEmpty = false)
    {
        if (!IsInMemory(address, 1))
        {
            return null;
        }

        var builder = new StringBuilder();
        for (int i = 0; i <= maximumLength; i++)
        {
            byte b = MemoryByte(address + i);
            if (b == 0)
            {
                return builder.Length > 0 || allowEmpty ? builder.ToString() : null;
            }

            if (b < 0x20 || b > 0x7E)
            {
                return null;
            }

            builder.Append((char)b);
        }

        return null;
    }

    /// <summary>
    /// A number in the variable-length encoding of the AOT compiler: 1, 2, 4
    /// or 5 bytes, most significant byte first.
    /// </summary>
    private uint ReadMonoValue(ref long address)
    {
        byte b = MemoryByte(address);
        if ((b & 0x80) == 0)
        {
            address++;
            return b;
        }

        if ((b & 0x40) == 0)
        {
            uint value = (uint)((b & 0x3F) << 8) | MemoryByte(address + 1);
            address += 2;
            return value;
        }

        if (b != 0xFF)
        {
            uint value = (uint)((b & 0x1F) << 24) | (uint)(MemoryByte(address + 1) << 16) | (uint)(MemoryByte(address + 2) << 8) | MemoryByte(address + 3);
            address += 4;
            return value;
        }

        uint result = (uint)(MemoryByte(address + 1) << 24) | (uint)(MemoryByte(address + 2) << 16) | (uint)(MemoryByte(address + 3) << 8) | MemoryByte(address + 4);
        address += 5;
        return result;
    }

    private bool IsMonoAotFileInfo(long info, long addressLimit)
    {
        for (int i = 0; i < MonoAotPointerCount; i++)
        {
            if (MemoryUInt32(info + 8 + i * 4) >= addressLimit)
            {
                return false;
            }
        }

        uint blob = MemoryUInt32(info + 8 + PointerBlob * 4);
        uint methodInfoOffsets = MemoryUInt32(info + 8 + PointerMethodInfoOffsets * 4);
        uint assemblyName = MemoryUInt32(info + 8 + PointerAssemblyName * 4);
        return IsInMemory(blob, 1) && IsInMemory(methodInfoOffsets, 16) && ReadMemoryString(assemblyName, 260) != null;
    }

    private void ReadMonoAotModule(long info, List<MemoryNode> nodes)
    {
        long scalars = info + 8 + MonoAotPointerCount * 4;
        uint Pointer(int index) => MemoryUInt32(info + 8 + index * 4);
        uint Scalar(int index) => MemoryUInt32(scalars + index * 4);

        string name = ReadMemoryString(Pointer(PointerAssemblyName), 260);
        var module = new MonoAotModule
        {
            Name = name,
            InfoAddress = info,
            MethodCount = (int)Scalar(ScalarMethodCount),
            AssemblyGuid = ReadMemoryString(Pointer(PointerAssemblyGuid), 64)
        };
        MonoAotModules.Add(module);

        var infoNode = new MemoryNode { Address = info, Length = MonoAotFileInfoSize, Text = $"Mono AOT file info: {name}" };
        nodes.Add(infoNode);
        infoNode.Add(info, 4, $"AOT file format version: {MonoAotFileVersion}");
        infoNode.Add(info + 4, 4, "Alignment", isPadding: true);
        for (int i = 0; i < MonoAotPointerCount; i++)
        {
            infoNode.Add(info + 8 + i * 4, 4, $"{monoAotPointerNames[i]}: 0x{Pointer(i):X}");
        }

        for (int i = 0; i < monoAotScalarNames.Length; i++)
        {
            string value = i == ScalarFlags ? $"0x{Scalar(i):X}" : Scalar(i).ToString();
            infoNode.Add(scalars + i * 4, 4, $"{monoAotScalarNames[i]}: {value}");
        }

        long position = scalars + monoAotScalarNames.Length * 4;
        foreach (var (arrayName, count) in monoAotArrayNames)
        {
            for (int i = 0; i < count; i++)
            {
                infoNode.Add(position, 4, $"{arrayName} [{i}]: {MemoryUInt32(position)}");
                position += 4;
            }
        }

        infoNode.Add(position, 16, "AOT id");

        // Offsets into the blob, with what is stored there
        var blobEntries = new Dictionary<uint, (string kind, string noun, int first, int count)>();
        void Register(uint offset, string kind, string noun, int index)
        {
            if (offset == 0)
            {
                return;
            }

            if (blobEntries.TryGetValue(offset, out var entry))
            {
                if (entry.kind == kind)
                {
                    blobEntries[offset] = (kind, noun, entry.first, entry.count + 1);
                }
            }
            else
            {
                blobEntries[offset] = (kind, noun, index, 1);
            }
        }

        module.MethodInfoOffsetsAddress = Pointer(PointerMethodInfoOffsets);
        module.MethodFlagsAddress = Pointer(PointerMethodFlagsTable);

        void OffsetTable(int pointer, string tableName, string kind, string noun, int firstIndex)
        {
            var offsets = AddMonoOffsetTable(Pointer(pointer), $"Mono AOT {tableName}: {name}", nodes);
            if (offsets != null)
            {
                if (pointer == PointerLlvmGotInfoOffsets)
                {
                    module.LlvmGotSlotCount = (uint)offsets.Length;
                }

                for (int i = 0; i < offsets.Length; i++)
                {
                    Register(offsets[i], kind, noun, i + firstIndex);
                }
            }
        }

        OffsetTable(PointerMethodInfoOffsets, "method info offsets", "Method info", "method", 0);
        OffsetTable(PointerExceptionInfoOffsets, "exception info offsets", "Exception info", "method", 0);
        OffsetTable(PointerClassInfoOffsets, "class info offsets", "Class info", "type", 1);
        OffsetTable(PointerGotInfoOffsets, "GOT info offsets", "GOT slot info", "slot", 0);
        OffsetTable(PointerLlvmGotInfoOffsets, "LLVM GOT info offsets", "LLVM GOT slot info", "slot", 0);

        // Extra methods are the ones without a metadata token of this
        // assembly, such as generic instances and wrappers: pairs of method
        // index and the blob offset of the description of the method
        long extraInfo = Pointer(PointerExtraMethodInfoOffsets);
        uint extraCount = MemoryUInt32(extraInfo);
        if (IsInMemory(extraInfo, 4 + 8L * extraCount) && extraCount < 0x1000000)
        {
            var node = new MemoryNode { Address = extraInfo, Length = (int)(4 + 8 * extraCount), Text = $"Mono AOT extra method info offsets: {name}" };
            node.Add(extraInfo, 4, $"Extra method count: {extraCount}");
            node.Add(extraInfo + 4, 8L * extraCount, $"Method index and blob offset of each extra method ({extraCount:N0} entries of 8 bytes)");
            nodes.Add(node);
            for (uint i = 0; i < extraCount; i++)
            {
                Register(MemoryUInt32(extraInfo + 8 + 8 * i), "Extra method description", "method", (int)MemoryUInt32(extraInfo + 4 + 8 * i));
            }
        }

        AddMonoExtraMethodTable(Pointer(PointerExtraMethodTable), name, nodes);
        AddMonoClassNameTable(Pointer(PointerClassNameTable), name, nodes);
        AddMonoImageTable(Pointer(PointerImageTable), name, nodes);

        long methodFlags = Pointer(PointerMethodFlagsTable);
        if (module.MethodCount > 0 && IsInMemory(methodFlags, module.MethodCount))
        {
            nodes.Add(new MemoryNode { Address = methodFlags, Length = module.MethodCount, Text = $"Mono AOT method flags: {name} (one byte for each of {module.MethodCount:N0} methods)" });
        }

        long weakFields = Pointer(PointerWeakFieldIndexes);
        uint weakFieldCount = MemoryUInt32(weakFields);
        if (IsInMemory(weakFields, 4 + 4L * weakFieldCount) && weakFieldCount < 0x1000000)
        {
            var node = new MemoryNode { Address = weakFields, Length = (int)(4 + 4 * weakFieldCount), Text = $"Mono AOT weak field indexes: {name}" };
            node.Add(weakFields, 4, $"Weak field count: {weakFieldCount}");
            node.Add(weakFields + 4, 4L * weakFieldCount, "Field indexes");
            nodes.Add(node);
        }

        uint unboxCount = Scalar(ScalarLlvmUnboxTrampolineCount);
        uint unboxIndexSize = Scalar(ScalarLlvmUnboxTrampolineIndexSize);
        long unboxIndexes = Pointer(PointerLlvmUnboxTrampolineIndexes);
        long unboxTrampolines = Pointer(PointerLlvmUnboxTrampolines);
        if (unboxCount > 0 && unboxCount < 0x1000000 && (unboxIndexSize == 2 || unboxIndexSize == 4))
        {
            if (IsInMemory(unboxIndexes, (long)unboxCount * unboxIndexSize))
            {
                nodes.Add(new MemoryNode { Address = unboxIndexes, Length = (int)(unboxCount * unboxIndexSize), Text = $"Mono AOT unbox trampoline method indexes: {name} ({unboxCount:N0} entries of {unboxIndexSize} bytes)" });
            }

            if (IsInMemory(unboxTrampolines, 4L * unboxCount))
            {
                module.UnboxTrampolinesAddress = unboxTrampolines;
                module.UnboxTrampolineIndexesAddress = unboxIndexes;
                module.UnboxTrampolineCount = unboxCount;
                module.UnboxTrampolineIndexSize = unboxIndexSize;
                nodes.Add(new MemoryNode { Address = unboxTrampolines, Length = (int)(4 * unboxCount), Text = $"Mono AOT unbox trampoline functions: {name} ({unboxCount:N0} function table indexes)" });
            }
        }

        AddMonoString(Pointer(PointerAssemblyName), "Mono AOT assembly name", nodes);
        AddMonoString(Pointer(PointerAssemblyGuid), "Mono AOT assembly GUID", nodes);
        AddMonoString(Pointer(PointerRuntimeVersion), "Mono AOT runtime version", nodes);

        // The name of the garbage collector is the last entry that the
        // compiler adds to the blob, so the blob ends after it
        long blob = Pointer(PointerBlob);
        uint gcNameOffset = Scalar(ScalarGcNameIndex);
        string gcName = ReadMemoryString(blob + gcNameOffset, 32);
        if (gcName == null || gcNameOffset > int.MaxValue / 2)
        {
            return;
        }

        int blobLength = (int)gcNameOffset + gcName.Length + 1;
        module.BlobLength = blobLength;
        blobEntries[gcNameOffset] = ($"GC name: {gcName}", null, 0, 1);

        var blobNode = new MemoryNode { Address = blob, Length = blobLength, Text = $"Mono AOT blob: {name}" };
        nodes.Add(blobNode);

        var offsetList = new List<uint>(blobEntries.Count);
        foreach (uint offset in blobEntries.Keys)
        {
            if (offset < blobLength)
            {
                offsetList.Add(offset);
            }
        }

        offsetList.Sort();

        // An entry is labelled from its offset to the offset of the next
        // entry. Type and method references that entries share are stored
        // between the entries and are inside these ranges.
        if (offsetList.Count > 0 && offsetList[0] > 0)
        {
            blobNode.Add(blob, offsetList[0], "Data before the first entry");
        }

        for (int i = 0; i < offsetList.Count; i++)
        {
            uint offset = offsetList[i];
            uint end = i + 1 < offsetList.Count ? offsetList[i + 1] : (uint)blobLength;
            var (kind, noun, firstIndex, count) = blobEntries[offset];
            string text = noun == null
                ? kind
                : count == 1 ? $"{kind}: {noun} {firstIndex}" : $"{kind}: {noun} {firstIndex} and {count - 1:N0} more";
            blobNode.Add(blob + offset, end - offset, text);
        }
    }

    /// <summary>
    /// The info records of the methods of one assembly. With LLVM, the
    /// compiler does not put them into the blob: each one is a separate
    /// constant, and the constants are stored one after another in front of
    /// the method info offsets. A record has the method index, a reference
    /// to the class whose static constructor has to run first when the
    /// method flags say so, and the GOT slots that the method uses.
    ///
    /// Where the records start is not stored anywhere. A position counts as
    /// the start of a record when the record is valid and is followed by
    /// another one with a higher method index, up to the offsets table.
    /// </summary>
    private void AddMonoMethodInfos(MonoAotModule module, List<MemoryNode> sortedNodes, List<MemoryNode> output)
    {
        const int MaximumGap = 0x400000;
        const int MinimumRecordSize = 6;
        const int Alignment = 16;

        long end = module.MethodInfoOffsetsAddress;
        if (module.MethodCount <= 0 || !IsInMemory(module.MethodFlagsAddress, module.MethodCount) || !IsInMemory(end, 1))
        {
            return;
        }

        // The records are located after the last table in front of them
        long start = Math.Max(memoryStart, end - MaximumGap);
        int lo = 0;
        int hi = sortedNodes.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (sortedNodes[mid].Address < end)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (hi >= 0)
        {
            if (sortedNodes[hi].End > end)
            {
                return;
            }

            start = Math.Max(start, sortedNodes[hi].End);
        }

        int size = (int)(end - start);
        if (size < MinimumRecordSize)
        {
            return;
        }

        // Zero bytes of alignment can follow the last record
        int tail = 0;
        while (tail < Alignment - 1 && tail < size && MemoryByte(end - 1 - tail) == 0)
        {
            tail++;
        }

        // From the end to the start: the length of the record at each
        // position that is followed by valid records up to the end, and
        // where the following record starts. A record of more than 16 bytes
        // is aligned to 16 bytes, with zero bytes in front of it.
        var lengths = new int[size + 1];
        var indexes = new uint[size + 1];
        var following = new int[size + 1];
        for (int i = size - MinimumRecordSize; i >= 0; i--)
        {
            int length = MeasureMonoMethodInfo(start + i, end, module, out uint index, out _, out _, out _);
            if (length <= 0)
            {
                continue;
            }

            int next = i + length;
            int aligned = next + (int)((Alignment - ((start + next) & (Alignment - 1))) & (Alignment - 1));
            int follower = -1;
            if (size - next <= tail)
            {
                follower = size;
            }
            else if (lengths[next] > 0 && indexes[next] > index && (lengths[next] <= Alignment || aligned == next))
            {
                follower = next;
            }
            else if (aligned < size && lengths[aligned] > Alignment && indexes[aligned] > index && IsZeroMemory(start + next, aligned - next))
            {
                follower = aligned;
            }

            if (follower >= 0)
            {
                lengths[i] = length;
                indexes[i] = index;
                following[i] = follower;
            }
        }

        int first = 0;
        while (first < size && lengths[first] == 0)
        {
            first++;
        }

        if (first >= size)
        {
            return;
        }

        var records = new List<MemoryNode>();
        int position = first;
        int recordCount = 0;
        int recordEnd = first;
        while (position < size && lengths[position] > 0)
        {
            if (position > recordEnd && DataSymbols.Count == 0)
            {
                records.Add(new MemoryNode { Address = start + recordEnd, Length = position - recordEnd, Text = "Padding", IsPadding = true });
            }

            long address = start + position;
            int length = MeasureMonoMethodInfo(address, end, module, out uint index, out int classReferenceLength, out int genericContextLength, out uint slotCount);
            var record = new MemoryNode { Address = address, Length = length, Text = $"Mono AOT method info: {module.Name}, method {index}" };
            record.Add(address, 4, $"Method index: {index}");
            long field = address + 4;
            if (classReferenceLength > 0)
            {
                record.Add(field, classReferenceLength, "Reference to the class with the static constructor");
                field += classReferenceLength;
            }

            if (genericContextLength > 0)
            {
                record.Add(field, genericContextLength, "Generic context");
                field += genericContextLength;
            }

            if (field < address + length)
            {
                long slots = field;
                ReadMonoValue(ref slots);
                record.Add(field, slots - field, $"GOT slot count: {slotCount}");
                record.Add(slots, address + length - slots, "GOT slot indexes");
            }

            records.Add(record);
            recordCount++;
            recordEnd = position + length;
            module.MethodInfoLength += length;
            position = following[position];
        }

        module.MethodInfoCount = recordCount;

        // In an object file every record has a data symbol of its own
        if (DataSymbols.Count > 0)
        {
            output.AddRange(records);
            return;
        }

        var parent = new MemoryNode
        {
            Address = start + first,
            Length = recordEnd - first,
            Text = $"Mono AOT method info: {module.Name} ({recordCount:N0} methods)",
            Children = records
        };
        foreach (var record in records)
        {
            if (!record.IsPadding)
            {
                record.Text = "Method " + record.Text.Substring(record.Text.LastIndexOf(' ') + 1);
            }
        }

        output.Add(parent);
    }

    private bool IsZeroMemory(long address, long length)
    {
        for (long i = 0; i < length; i++)
        {
            if (MemoryByte(address + i) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Length of the method info record at the address, or 0 when the bytes
    /// are not a valid record of the assembly.
    /// </summary>
    private int MeasureMonoMethodInfo(long address, long limit, MonoAotModule module, out uint index, out int classReferenceLength, out int genericContextLength, out uint slotCount)
    {
        const int HasStaticConstructor = 1;
        const int HasPatches = 4;
        const int HasGenericContext = 8;

        classReferenceLength = 0;
        genericContextLength = 0;
        slotCount = 0;
        index = MemoryUInt32(address);
        if (address + 5 > limit || index >= module.MethodCount)
        {
            return 0;
        }

        byte flags = MemoryByte(module.MethodFlagsAddress + index);
        if ((flags & (HasStaticConstructor | HasPatches | HasGenericContext)) == 0)
        {
            return 0;
        }

        long position = address + 4;
        if ((flags & HasStaticConstructor) != 0)
        {
            if (!SkipMonoClassReference(ref position, limit, 0))
            {
                return 0;
            }

            classReferenceLength = (int)(position - address - 4);
        }

        // A generic context: which of the two instantiations are present,
        // then the blob offset of each
        if ((flags & HasGenericContext) != 0)
        {
            if (position >= limit)
            {
                return 0;
            }

            long contextStart = position;
            uint parts = ReadMonoValue(ref position);
            if (parts == 0 || parts > 3)
            {
                return 0;
            }

            if ((parts & 1) != 0)
            {
                ReadMonoValue(ref position);
            }

            if ((parts & 2) != 0)
            {
                ReadMonoValue(ref position);
            }

            genericContextLength = (int)(position - contextStart);
        }

        if ((flags & HasPatches) != 0)
        {
            if (position >= limit)
            {
                return 0;
            }

            slotCount = ReadMonoValue(ref position);
            if (slotCount == 0 || slotCount > limit - position)
            {
                return 0;
            }

            for (uint i = 0; i < slotCount; i++)
            {
                if (position >= limit || ReadMonoValue(ref position) >= module.LlvmGotSlotCount)
                {
                    return 0;
                }
            }
        }

        return position <= limit ? (int)(position - address) : 0;
    }

    /// <summary>
    /// Skips an encoded reference to a type. The kinds that need a full
    /// type decoder are reported as not valid.
    /// </summary>
    private bool SkipMonoClassReference(ref long position, long limit, int depth)
    {
        const int TypeDefIndex = 1;
        const int TypeDefIndexAndImage = 2;
        const int TypeSpecToken = 3;
        const int GenericInstance = 4;
        const int Array = 6;
        const int BlobIndex = 7;

        if (position >= limit || depth > 8)
        {
            return false;
        }

        uint kind = ReadMonoValue(ref position);
        switch (kind)
        {
            case TypeDefIndex:
            case TypeSpecToken:
            case BlobIndex:
                ReadMonoValue(ref position);
                break;
            case TypeDefIndexAndImage:
                ReadMonoValue(ref position);
                ReadMonoValue(ref position);
                break;
            case Array:
                ReadMonoValue(ref position);
                return SkipMonoClassReference(ref position, limit, depth + 1);
            case GenericInstance:
                if (!SkipMonoClassReference(ref position, limit, depth + 1))
                {
                    return false;
                }

                uint argumentCount = ReadMonoValue(ref position);
                if (argumentCount > 64)
                {
                    return false;
                }

                for (uint i = 0; i < argumentCount; i++)
                {
                    if (!SkipMonoClassReference(ref position, limit, depth + 1))
                    {
                        return false;
                    }
                }

                break;
            default:
                return false;
        }

        return position <= limit;
    }

    /// <summary>
    /// Records that the function at a position of the function table
    /// belongs to an assembly, and names the function after its role when
    /// the module has no name for it.
    /// </summary>
    private void SetFunctionOwner(uint tableIndex, MonoAotModule module, string role, int methodIndex = -1, bool isUnboxTrampoline = false)
    {
        if (tableIndex == 0 || !TableFunctions.TryGetValue(tableIndex, out int functionIndex))
        {
            return;
        }

        SetFunctionOwner(functionIndex, module, role, methodIndex, isUnboxTrampoline);
    }

    private void SetFunctionOwner(int functionIndex, MonoAotModule module, string role, int methodIndex, bool isUnboxTrampoline)
    {
        int position = functionIndex - ImportedFunctionCount;
        if (position < 0 || position >= Functions.Count)
        {
            return;
        }

        var function = Functions[position];
        if (function.Owner != null)
        {
            return;
        }

        function.Owner = module;
        function.MethodIndex = methodIndex;
        function.IsUnboxTrampoline = isUnboxTrampoline;
        module.FunctionCount++;
        module.CodeLength += function.Length;
        if (!FunctionNames.ContainsKey(functionIndex))
        {
            FunctionNames[functionIndex] = $"{module.Name}, {role}";
            function.Text = DescribeFunction(functionIndex);
        }
    }

    /// <summary>
    /// In an object file the method table and the unbox trampoline table
    /// are data symbols, and each entry has a relocation that names the
    /// function.
    /// </summary>
    private void SetFunctionOwnersFromRelocations(MonoAotModule module)
    {
        const string Prefix = "mono_aot_";
        foreach (var symbol in DataSymbols)
        {
            if (!symbol.name.StartsWith(Prefix, StringComparison.Ordinal) || symbol.segment < 0 || symbol.segment >= DataSegments.Count)
            {
                continue;
            }

            bool isMethodTable = symbol.name.EndsWith("_method_table", StringComparison.Ordinal);
            bool isUnboxTable = symbol.name.EndsWith("_unbox_trampolines", StringComparison.Ordinal);
            if (!isMethodTable && !isUnboxTable)
            {
                continue;
            }

            int start = DataSegments[symbol.segment].ContentStart + symbol.offset;
            int count = symbol.size / 4;
            for (int i = 0; i < count; i++)
            {
                if (!FunctionPointerRelocations.TryGetValue(start + 4 * i, out int symbolIndex) ||
                    symbolIndex < 0 || symbolIndex >= SymbolFunctions.Count || SymbolFunctions[symbolIndex] < 0)
                {
                    continue;
                }

                int methodIndex = i;
                if (isUnboxTable)
                {
                    if (i >= module.UnboxTrampolineCount)
                    {
                        break;
                    }

                    long indexAddress = module.UnboxTrampolineIndexesAddress + (long)i * module.UnboxTrampolineIndexSize;
                    methodIndex = module.UnboxTrampolineIndexSize == 2 ? MemoryUInt16(indexAddress) : (int)MemoryUInt32(indexAddress);
                }

                SetFunctionOwner(SymbolFunctions[symbolIndex], module, isUnboxTable ? $"unbox trampoline of method {methodIndex}" : $"method {methodIndex}", methodIndex, isUnboxTable);
            }
        }
    }

    private Dictionary<string, WasmFunction> functionsByName;

    /// <summary>
    /// The function with the given name, from the names that the module has
    /// for its functions.
    /// </summary>
    private WasmFunction FindFunction(string name)
    {
        if (functionsByName == null)
        {
            functionsByName = new Dictionary<string, WasmFunction>();
            const int CodeSection = 10;
            foreach (var section in Sections)
            {
                if (section.Id != CodeSection || !section.HasChildren)
                {
                    continue;
                }

                foreach (var child in section.Children)
                {
                    if (child is WasmFunction function && FunctionNames.TryGetValue(function.Index, out string functionName))
                    {
                        functionsByName[functionName] = function;
                    }
                }
            }
        }

        functionsByName.TryGetValue(name, out var result);
        return result;
    }

    /// <summary>
    /// The table with the function of every method of an assembly, by
    /// method index. It is located after the file info. No pointer to it is
    /// stored in the data: the get_method function of the assembly reads it.
    /// </summary>
    private void AddMonoMethodTable(MonoAotModule module, List<MemoryNode> output)
    {
        if (FunctionTableSize <= 0 || module.MethodCount <= 0 || DataSymbols.Count > 0)
        {
            return;
        }

        // The name of the function has every character of the assembly name
        // that is not a letter or a digit replaced with an underscore. The
        // core library has a name of its own.
        var symbol = new StringBuilder("mono_aot_");
        foreach (char c in module.Name)
        {
            symbol.Append(char.IsLetterOrDigit(c) && c < 0x80 ? c : '_');
        }

        symbol.Append("_get_method");
        var function = FindFunction(symbol.ToString());
        if (function == null && module.Name == "System.Private.CoreLib")
        {
            function = FindFunction("mono_aot_corlib_get_method");
        }

        if (function == null)
        {
            return;
        }

        WasmCode code = null;
        foreach (var child in function.Children)
        {
            code = child as WasmCode ?? code;
        }

        if (code == null)
        {
            return;
        }

        // The function compares the method index with the highest index
        // that the table has, and reads the table at a constant address.
        // The table and the file info are stored near each other.
        const int SearchRange = 0x400000;
        var candidates = new List<long>();
        int entryCount = module.MethodCount;
        int previousOpcode = 0;
        long previousOperand = 0;
        int beforePreviousOpcode = 0;
        bool isDecoded = WasmInstructions.Decode(Bytes, code.Start, code.End, (opcode, operand) =>
        {
            if (opcode == WasmInstructions.I32Const || opcode == WasmInstructions.I32Load)
            {
                long address = (uint)operand;
                if (Math.Abs(address - module.InfoAddress) < SearchRange && (address & 3) == 0)
                {
                    candidates.Add(address);
                }
            }

            if (previousOpcode == WasmInstructions.I32Const && beforePreviousOpcode == WasmInstructions.LocalGet && previousOperand > 0 && previousOperand <= module.MethodCount)
            {
                if (opcode == WasmInstructions.I32GreaterThanSigned || opcode == WasmInstructions.I32GreaterThanUnsigned ||
                    opcode == WasmInstructions.I32LessOrEqualSigned || opcode == WasmInstructions.I32LessOrEqualUnsigned)
                {
                    entryCount = (int)Math.Min(previousOperand + 1, module.MethodCount);
                }
                else if (opcode == WasmInstructions.I32GreaterOrEqualSigned || opcode == WasmInstructions.I32GreaterOrEqualUnsigned ||
                    opcode == WasmInstructions.I32LessThanSigned || opcode == WasmInstructions.I32LessThanUnsigned)
                {
                    entryCount = (int)previousOperand;
                }
            }

            beforePreviousOpcode = previousOpcode;
            previousOpcode = opcode;
            previousOperand = operand;
        });
        if (!isDecoded)
        {
            return;
        }

        long length = 4L * entryCount;
        foreach (long address in candidates)
        {
            if (!IsInMemory(address, length))
            {
                continue;
            }

            int used = 0;
            bool isValid = true;
            for (int i = 0; i < entryCount; i++)
            {
                uint value = MemoryUInt32(address + 4L * i);
                if (value >= FunctionTableSize)
                {
                    isValid = false;
                    break;
                }

                if (value != 0)
                {
                    used++;
                }
            }

            if (isValid && used > 0)
            {
                for (int i = 0; i < entryCount; i++)
                {
                    SetFunctionOwner(MemoryUInt32(address + 4L * i), module, $"method {i}", i);
                }

                for (uint i = 0; i < module.UnboxTrampolineCount; i++)
                {
                    long indexAddress = module.UnboxTrampolineIndexesAddress + i * module.UnboxTrampolineIndexSize;
                    uint methodIndex = module.UnboxTrampolineIndexSize == 2 ? MemoryUInt16(indexAddress) : MemoryUInt32(indexAddress);
                    SetFunctionOwner(MemoryUInt32(module.UnboxTrampolinesAddress + 4L * i), module, $"unbox trampoline of method {methodIndex}", (int)methodIndex, isUnboxTrampoline: true);
                }

                string[] helpers = ["get_method", "get_unbox_tramp", "init_aotconst"];
                for (int i = 0; i < helpers.Length; i++)
                {
                    SetFunctionOwner(MemoryUInt32(module.InfoAddress + 8 + (2 + i) * 4), module, helpers[i]);
                }

                output.Add(new MemoryNode
                {
                    Address = address,
                    Length = (int)length,
                    Text = $"Mono AOT method table: {module.Name} ({entryCount:N0} function table indexes, {used:N0} of them set)"
                });
                return;
            }
        }
    }

    private void AddMonoString(long address, string label, List<MemoryNode> nodes)
    {
        if (address == 0)
        {
            return;
        }

        string value = ReadMemoryString(address, 260, allowEmpty: true);
        if (value != null)
        {
            nodes.Add(new MemoryNode { Address = address, Length = value.Length + 1, Text = $"{label}: \"{value}\"" });
        }
    }

    /// <summary>
    /// A table of numbers in a compact form: a header of four integers, an
    /// index with the position of every group, and the groups. A group has
    /// its first number in full and the following ones as differences.
    /// Returns the numbers.
    /// </summary>
    private uint[] AddMonoOffsetTable(long address, string text, List<MemoryNode> nodes)
    {
        const int HeaderSize = 16;
        if (address == 0 || !IsInMemory(address, HeaderSize))
        {
            return null;
        }

        uint count = MemoryUInt32(address);
        uint groupSize = MemoryUInt32(address + 4);
        uint groupCount = MemoryUInt32(address + 8);
        uint indexEntrySize = MemoryUInt32(address + 12);
        if (groupSize == 0 || groupSize > 0x10000 || count > 0x4000000 ||
            (indexEntrySize != 2 && indexEntrySize != 4) ||
            groupCount != (count + groupSize - 1) / groupSize)
        {
            return null;
        }

        long index = address + HeaderSize;
        long data = index + (long)groupCount * indexEntrySize;
        if (!IsInMemory(data, count))
        {
            return null;
        }

        var offsets = new uint[count];
        long position = data;
        uint current = 0;
        for (uint i = 0; i < count; i++)
        {
            uint value = ReadMonoValue(ref position);
            current = i % groupSize == 0 ? value : current + value;
            offsets[i] = current;
        }

        if (!IsInMemory(address, position - address))
        {
            return null;
        }

        var node = new MemoryNode { Address = address, Length = (int)(position - address), Text = text };
        node.Add(address, 4, $"Offset count: {count}");
        node.Add(address + 4, 4, $"Group size: {groupSize}");
        node.Add(address + 8, 4, $"Group count: {groupCount}");
        node.Add(address + 12, 4, $"Index entry size: {indexEntrySize}");
        node.Add(index, data - index, $"Index ({groupCount:N0} entries of {indexEntrySize} bytes)");
        node.Add(data, position - data, $"Encoded offsets ({count:N0})");
        nodes.Add(node);
        return offsets;
    }

    /// <summary>
    /// A hash table from the description of an extra method to its method
    /// index: five integers, then entries of key, value and index of the
    /// next entry with the same hash.
    /// </summary>
    private void AddMonoExtraMethodTable(long address, string name, List<MemoryNode> nodes)
    {
        const int HeaderSize = 20;
        if (address == 0 || !IsInMemory(address, HeaderSize))
        {
            return;
        }

        uint tableSize = MemoryUInt32(address);
        uint entryCount = MemoryUInt32(address + 4);
        uint keySize = MemoryUInt32(address + 8);
        uint valueSize = MemoryUInt32(address + 12);
        uint nextSize = MemoryUInt32(address + 16);
        bool IsFieldSize(uint size) => size == 1 || size == 2 || size == 4;
        if (!IsFieldSize(keySize) || !IsFieldSize(valueSize) || !IsFieldSize(nextSize) || entryCount < tableSize || entryCount > 0x4000000)
        {
            return;
        }

        uint entrySize = keySize + valueSize + nextSize;
        long length = HeaderSize + (long)entryCount * entrySize;
        if (!IsInMemory(address, length))
        {
            return;
        }

        var node = new MemoryNode { Address = address, Length = (int)length, Text = $"Mono AOT extra method table: {name}" };
        node.Add(address, 4, $"Hash table size: {tableSize}");
        node.Add(address + 4, 4, $"Entry count: {entryCount}");
        node.Add(address + 8, 4, $"Key size: {keySize}");
        node.Add(address + 12, 4, $"Value size: {valueSize}");
        node.Add(address + 16, 4, $"Next index size: {nextSize}");
        node.Add(address + HeaderSize, length - HeaderSize, $"Entries: blob offset, method index, next entry ({entryCount:N0} entries of {entrySize} bytes)");
        nodes.Add(node);
    }

    /// <summary>
    /// A hash table from the name of a type to its index: the table size,
    /// then entries of type index and index of the next entry with the same
    /// hash. Entries past the table size are the ones that the chains use.
    /// </summary>
    private void AddMonoClassNameTable(long address, string name, List<MemoryNode> nodes)
    {
        if (address == 0 || !IsInMemory(address, 2))
        {
            return;
        }

        int tableSize = MemoryUInt16(address);
        int entryCount = tableSize;
        for (int i = 0; i < entryCount; i++)
        {
            int next = MemoryUInt16(address + 2 + i * 4 + 2);
            if (next >= entryCount)
            {
                entryCount = next + 1;
            }
        }

        long length = 2 + 4L * entryCount;
        if (tableSize == 0 || !IsInMemory(address, length))
        {
            return;
        }

        var node = new MemoryNode { Address = address, Length = (int)length, Text = $"Mono AOT class name table: {name}" };
        node.Add(address, 2, $"Hash table size: {tableSize}");
        node.Add(address + 2, length - 2, $"Entries: type index, next entry ({entryCount:N0} entries of 4 bytes)");
        nodes.Add(node);
    }

    /// <summary>
    /// The assemblies that the compiled code refers to: a count, then for
    /// each assembly its name, module version id, culture, public key token,
    /// padding to 8 bytes, flags and the four parts of the version.
    /// </summary>
    private void AddMonoImageTable(long address, string name, List<MemoryNode> nodes)
    {
        if (address == 0 || !IsInMemory(address, 4))
        {
            return;
        }

        uint count = MemoryUInt32(address);
        if (count == 0 || count > 0x10000)
        {
            return;
        }

        var node = new MemoryNode { Address = address, Text = $"Mono AOT image table: {name} ({count:N0} assemblies)" };
        node.Add(address, 4, $"Assembly count: {count}");
        long position = address + 4;
        string[] labels = ["Name", "Module version id", "Culture", "Public key token"];
        for (uint i = 0; i < count; i++)
        {
            long start = position;
            var strings = new string[labels.Length];
            for (int s = 0; s < labels.Length; s++)
            {
                strings[s] = ReadMemoryString(position, 1024, allowEmpty: true);
                if (strings[s] == null)
                {
                    return;
                }

                position += strings[s].Length + 1;
            }

            long aligned = address + ((position - address + 7) & ~7L);
            long end = aligned + 20;
            if (!IsInMemory(start, end - start))
            {
                return;
            }

            var image = node.Add(start, end - start, $"Assembly {i}: {strings[0]}");
            long stringStart = start;
            for (int s = 0; s < labels.Length; s++)
            {
                image.Add(stringStart, strings[s].Length + 1, $"{labels[s]}: \"{strings[s]}\"");
                stringStart += strings[s].Length + 1;
            }

            image.Add(position, aligned - position, "Padding", isPadding: true);
            image.Add(aligned, 4, $"Flags: 0x{MemoryUInt32(aligned):X}");
            image.Add(aligned + 4, 16, $"Version: {MemoryUInt32(aligned + 4)}.{MemoryUInt32(aligned + 8)}.{MemoryUInt32(aligned + 12)}.{MemoryUInt32(aligned + 16)}");
            position = end;
        }

        node.Length = (int)(position - address);
        nodes.Add(node);
    }

    /// <summary>
    /// Index of the first data segment that ends after the address.
    /// </summary>
    private int FindSegmentIndex(long address)
    {
        int lo = 0;
        int hi = segmentsByAddress.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var segment = segmentsByAddress[mid];
            if (segment.Address + segment.ContentLength <= address)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private void AddFileNodes(MemoryNode source, Dictionary<int, List<Node>> output)
    {
        for (int i = FindSegmentIndex(source.Address); i < segmentsByAddress.Count; i++)
        {
            var segment = segmentsByAddress[i];
            if (segment.Address >= source.End)
            {
                break;
            }

            long from = Math.Max(source.Address, segment.Address);
            long to = Math.Min(source.End, segment.Address + segment.ContentLength);
            if (to <= from)
            {
                continue;
            }

            if (!output.TryGetValue(segment.Index, out var list))
            {
                list = new List<Node>();
                output[segment.Index] = list;
            }

            int fileStart = segment.ContentStart + (int)(from - segment.Address);
            list.Add(CreateFileNode(source, from, (int)(to - from), fileStart));

            // The text is "Mono AOT", the kind of data, a colon and the assembly
            const string Prefix = "Mono AOT ";
            int colon = source.Text.IndexOf(':');
            if (source.Module != null && colon > Prefix.Length && source.Text.StartsWith(Prefix))
            {
                string kind = source.Text.Substring(Prefix.Length, colon - Prefix.Length);
                source.Module.DataLengths.TryGetValue(kind, out long bytes);
                source.Module.DataLengths[kind] = bytes + to - from;
                source.Module.DataRanges.Add((fileStart, (int)(to - from)));
            }
        }
    }

    /// <summary>
    /// The node for the part of a memory range that is stored at the file
    /// offset, with the parts of its children that are inside that part.
    /// </summary>
    private Node CreateFileNode(MemoryNode source, long address, int length, int fileStart)
    {
        Node node = source.IsPadding ? new Padding() : new Node { Text = source.Text };
        node.Buffer = Buffer;
        node.Start = fileStart;
        node.Length = length;
        if (source.Children == null)
        {
            return node;
        }

        long end = address + length;
        long covered = address;
        void AddChild(Node child)
        {
            child.Parent = node;
            node.Children.Add(child);
        }

        void AddGap(long upTo)
        {
            if (upTo > covered)
            {
                AddChild(new Unknown { Buffer = Buffer, Start = fileStart + (int)(covered - address), Length = (int)(upTo - covered), Text = "Not decoded" });
            }
        }

        int lo = 0;
        int hi = source.Children.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (source.Children[mid].End <= address)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (int i = lo; i < source.Children.Count; i++)
        {
            var child = source.Children[i];
            if (child.Address >= end)
            {
                break;
            }

            long from = Math.Max(child.Address, covered);
            long to = Math.Min(child.End, end);
            if (to <= from)
            {
                continue;
            }

            AddGap(from);
            AddChild(CreateFileNode(child, from, (int)(to - from), fileStart + (int)(from - address)));
            covered = to;
        }

        AddGap(end);
        return node;
    }
}
