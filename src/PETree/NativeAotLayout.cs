using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;

/// <summary>
/// Where the nodes of the map file of a NativeAOT image are in the file.
/// The map file of the compiler (IlcGenerateMapFile) lists every node of
/// the image with its kind, name, length and a hash of its bytes, and
/// without addresses. The nodes are listed in the order in which the
/// compiler writes them, and that is the order in the file for the nodes
/// of one section.
///
/// Methods: the image has the start and the length of each function in
/// its exception table. A method is one function, or several that follow
/// one another: the body and the handlers of its try blocks. The methods
/// are matched to the functions in order: a method starts at a function
/// start and ends at a function end, exactly its length later.
///
/// Data: the MethodTables are found by the reader of the image. Each one
/// is matched to the type node of the map file with the same length, in
/// order. The nodes that the map file lists between two types are stored
/// between the two MethodTables, one after another. A node without
/// addresses in it has the same bytes in the file as in the map file, so
/// its place is checked with the hash. The hash is of the bytes before
/// relocation: a slot with an address is zero there, so a node with
/// addresses is checked with its relocated slots set to zero.
///
/// Addresses: some nodes are found by an address that is stored in the
/// image. The unwind data of a method has the address of its exception
/// handling info. The GC static region is a list of the addresses of the
/// GC statics of all types, and the entry of a type has the addresses of
/// its MethodTable and of its initial values. The non-GC statics of a
/// type with a static constructor start with the address of that
/// constructor.
/// </summary>
internal sealed class NativeAotLayout
{
    private const string MethodCodeKind = "MethodCode";
    private const string HeaderKind = "ReadyToRunHeader";
    private const string ExceptionInfoKind = "MethodExceptionHandlingInfo";
    private const string AssociatedDataKind = "MethodAssociatedData";
    private const string CompositionKind = "GenericComposition";
    private const string CompositionText = "'Generic composition'";
    private const string GCStaticsKind = "GCStatics";
    private const string NonGCStaticsKind = "NonGCStatics";
    private const string PreInitDataKind = "GCStaticsPreInitData";
    private const string MethodFixupKind = "PInvokeMethodFixup";
    private const string ModuleFixupKind = "PInvokeModuleFixup";

    /// <summary>
    /// Functions that may be between two methods and belong to no method.
    /// </summary>
    private const int MaximumSkippedFunctions = 200;

    /// <summary>
    /// The kinds of data that are stored between the MethodTables, with the
    /// text for the node of the tree.
    /// </summary>
    private static readonly Dictionary<string, string> dataKinds = new()
    {
        ["MethodGenericDictionary"] = "Method generic dictionary",
        ["FieldRvaData"] = "Field data",
        ["MethodReadOnlyData"] = "Read-only data of a method",
        ["GCStaticsPreInitData"] = "Initial values of GC statics",
        ["Blob"] = "Blob",
        ["FatFunctionPointer"] = "Fat function pointer",
        ["TypeThreadStaticIndex"] = "Thread static index of a type",
        ["RuntimeConfigurationBlob"] = "Runtime configuration",
        ["ManagedDataDescriptor"] = "Managed data descriptor",

        // MethodTables that the reader of the image does not find
        ["AsyncContinuationEEType"] = "MethodTable of an async continuation",
        ["DataOnlyEEType"] = "MethodTable for data only"
    };

    /// <summary>
    /// The kinds that are stored after the generic compositions.
    /// </summary>
    private static readonly Dictionary<string, string> byteSequenceKinds = new()
    {
        ["GenericVariance"] = "Generic variance"
    };

    /// <summary>
    /// The kinds that are found by an address in the image, or between
    /// nodes that the reader of the image has found.
    /// </summary>
    private static readonly Dictionary<string, string> addressedKinds = new()
    {
        [CompositionKind] = "Generic composition",
        [ExceptionInfoKind] = "Exception handling info of a method",
        [AssociatedDataKind] = "Associated data of a method",
        [GCStaticsKind] = "GC statics",
        [NonGCStaticsKind] = "Non-GC statics",
        [MethodFixupKind] = "P/Invoke method fixup",
        [ModuleFixupKind] = "P/Invoke module fixup"
    };

    /// <summary>
    /// The kinds of data that have no addresses in them: their bytes in the
    /// file are the bytes that the hash in the map file is of.
    /// </summary>
    private static readonly HashSet<string> plainDataKinds = ["FieldRvaData", "RuntimeConfigurationBlob"];

    /// <summary>
    /// A hash of fewer bytes than this can match by chance, such as for a
    /// few zero bytes, and does not count as a check.
    /// </summary>
    private const int MinimumCheckedLength = 16;

    /// <summary>
    /// How far a node with a hash is looked for: the number of places
    /// times the length of the node is kept under the budget.
    /// </summary>
    private const int SearchBudget = 64 * 1024 * 1024;
    private const int MinimumSearchWindow = 256;
    private const int MaximumSearchWindow = 16384;

    private sealed class MapNode
    {
        public string Kind;
        public string Name;
        public int Length;
        public string Hash;
        public int Start = -1;

        /// <summary>
        /// The bytes at the place have the hash of the map file.
        /// </summary>
        public bool IsVerified;

        /// <summary>
        /// The place is the target of an address that the image stores.
        /// </summary>
        public bool IsAddressed;

        /// <summary>
        /// The exception handling info of a method.
        /// </summary>
        public MapNode ExceptionInfo;

        /// <summary>
        /// The associated data of a method.
        /// </summary>
        public MapNode AssociatedData;

        /// <summary>
        /// Other nodes with the same bytes that the linker stored once, at this place.
        /// </summary>
        public int SharedCount;
    }

    private readonly List<MapNode> nodes = new();
    private readonly List<MapNode> methods = new();
    private readonly List<NativeCode> functions = new();
    private readonly HashSet<int> methodTableStarts = new();
    private PEFile file;
    private int[] relocations = [];

    /// <summary>
    /// For each method: the index of its first function and the number of
    /// its functions, or -1 when the method was not found.
    /// </summary>
    private int[] firstFunction;
    private int[] functionCount;

    public byte[] FileBytes { get; private set; }
    public string Message { get; private set; }
    public string DataMessage { get; private set; }
    public List<string> Details { get; } = new();
    public int MethodCount => methods.Count;
    public int FoundCount { get; private set; }

    public static NativeAotLayout Read(PEFile file, string mapPath)
    {
        var layout = new NativeAotLayout();
        layout.file = file;
        layout.relocations = file.RelocationOffsets.ToArray();
        layout.FileBytes = file.Buffer.ReadBytes(0, (int)file.Buffer.Length);
        using (var reader = XmlReader.Create(mapPath))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && int.TryParse(reader.GetAttribute("Length"), out int length))
                {
                    var node = new MapNode { Kind = reader.Name, Name = reader.GetAttribute("Name") ?? "", Length = length, Hash = reader.GetAttribute("Hash") };
                    layout.nodes.Add(node);
                    if (node.Kind == MethodCodeKind)
                    {
                        layout.methods.Add(node);
                    }
                }
            }
        }

        // The exception handling info of a method has the name of the method
        const string ExceptionInfoPrefix = "__ehinfo_";
        var methodsByName = new Dictionary<string, MapNode>();
        foreach (var method in layout.methods)
        {
            methodsByName.TryAdd(method.Name, method);
        }

        const string AssociatedDataPrefix = "_associatedData_";
        foreach (var node in layout.nodes)
        {
            if (node.Kind == ExceptionInfoKind && node.Name.StartsWith(ExceptionInfoPrefix, StringComparison.Ordinal) &&
                methodsByName.TryGetValue(node.Name.Substring(ExceptionInfoPrefix.Length), out var method))
            {
                method.ExceptionInfo ??= node;
            }
            else if (node.Kind == AssociatedDataKind && node.Name.StartsWith(AssociatedDataPrefix, StringComparison.Ordinal) &&
                methodsByName.TryGetValue(node.Name.Substring(AssociatedDataPrefix.Length), out method))
            {
                method.AssociatedData ??= node;
            }
        }

        // The managed code is in the first section
        var section = file.Children.OfType<Section>().FirstOrDefault();
        if (section != null)
        {
            layout.Collect(section);
        }

        layout.functions.Sort((left, right) => left.Start.CompareTo(right.Start));
        layout.Match(section?.Start ?? 0);
        layout.PlaceData(file);
        return layout;
    }

    private void Collect(Node node)
    {
        if (node is NativeCode code)
        {
            functions.Add(code);
            return;
        }

        if (node.HasChildren)
        {
            foreach (var child in node.Children)
            {
                Collect(child);
            }
        }
    }

    private void Match(int position)
    {
        firstFunction = new int[methods.Count];
        functionCount = new int[methods.Count];
        int index = 0;
        for (int i = 0; i < methods.Count; i++)
        {
            firstFunction[i] = -1;
            int length = methods[i].Length;
            while (index < functions.Count && functions[index].Start < position)
            {
                index++;
            }

            for (int attempt = 0; attempt < MaximumSkippedFunctions && index + attempt < functions.Count; attempt++)
            {
                int start = functions[index + attempt].Start;
                int end = start;
                int next = index + attempt;
                while (next < functions.Count && functions[next].Start == end && end - start < length)
                {
                    end += functions[next].Length;
                    next++;
                }

                if (end - start == length)
                {
                    firstFunction[i] = index + attempt;
                    functionCount[i] = next - index - attempt;
                    methods[i].Start = start;
                    position = end;
                    index = next;
                    FoundCount++;
                    break;
                }
            }
        }

        if (FoundCount == 0)
        {
            Message = $"Map file: none of its {methods.Count:N0} methods was found in the image. The map file is from another build.";
            return;
        }

        int firstFound = Array.FindIndex(firstFunction, f => f >= 0);
        int lastFound = Array.FindLastIndex(firstFunction, f => f >= 0);
        int from = functions[firstFunction[firstFound]].Start;
        var lastNode = functions[firstFunction[lastFound] + functionCount[lastFound] - 1];
        Message = $"Map file: {FoundCount:N0} of its {methods.Count:N0} methods were found in the image, in the order of the map file, at file offsets 0x{from:X} to 0x{lastNode.End:X}.";
    }

    /// <summary>
    /// Gives the file offset of the code to each method of another list of
    /// the same methods, such as the one of the mstat file. The two lists
    /// have to agree in the number of methods and in every length.
    /// </summary>
    public bool Assign(int count, Func<int, long> getLength, Func<int, string> getName, Action<int, int> setStart)
    {
        if (count != methods.Count)
        {
            Message += $" The mstat file has {count:N0} methods: the two files are not from the same build, and no file offsets are used.";
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (getLength(i) != methods[i].Length)
            {
                Message += $" Method {i:N0} has a length of {getLength(i):N0} in the mstat file and of {methods[i].Length:N0} in the map file: the two files are not from the same build, and no file offsets are used.";
                return false;
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (firstFunction[i] < 0)
            {
                continue;
            }

            setStart(i, functions[firstFunction[i]].Start);
            string name = getName(i);
            for (int part = 0; part < functionCount[i]; part++)
            {
                var node = functions[firstFunction[i] + part];
                node.Text = part == 0 ? $"Native code: {name}" : $"Native code: {name} (part {part + 1})";
            }
        }

        Message += " The mstat file lists the same methods with the same lengths.";
        return true;
    }

    private static bool IsGeneric(Node node) => node is Unknown || node is Padding || node is PointerTable || node is StringLiteral;

    private const int NoMatch = 0;

    /// <summary>
    /// The bytes have the hash when the relocated slots are set to zero,
    /// and no other byte is set: every slot is an address or zero.
    /// </summary>
    private const int ZeroMatch = 1;
    private const int HashMatch = 2;

    private static bool HasHash(MapNode node, ReadOnlySpan<byte> bytes) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), node.Hash, StringComparison.OrdinalIgnoreCase);

    private bool HasHash(MapNode node, int start) => Compare(node, start, withRelocations: false) == HashMatch;

    /// <summary>
    /// Compares the bytes at a place with the hash of a node. The hash of
    /// the map file is of the bytes before relocation, where a slot with
    /// an address is zero unless a number is added to the address.
    /// </summary>
    private int Compare(MapNode node, int start, bool withRelocations)
    {
        if (node.Hash == null || node.Length == 0 || start < 0 || start + node.Length > FileBytes.Length)
        {
            return NoMatch;
        }

        var bytes = FileBytes.AsSpan(start, node.Length);
        if (HasHash(node, bytes))
        {
            return HashMatch;
        }

        if (!withRelocations)
        {
            return NoMatch;
        }

        int size = file.PointerSize;
        int end = start + node.Length;
        int first = FirstRelocation(start - size + 1);
        if (first >= relocations.Length || relocations[first] >= end)
        {
            return NoMatch;
        }

        var copy = bytes.ToArray();
        for (int i = first; i < relocations.Length && relocations[i] < end; i++)
        {
            // A slot that is partly outside is a slot of another node
            if (relocations[i] < start || relocations[i] + size > end)
            {
                return NoMatch;
            }

            Array.Clear(copy, relocations[i] - start, size);
        }

        if (!HasHash(node, copy))
        {
            return NoMatch;
        }

        return copy.AsSpan().IndexOfAnyExcept((byte)0) >= 0 ? HashMatch : ZeroMatch;
    }

    private int FirstRelocation(int offset)
    {
        int index = Array.BinarySearch(relocations, offset);
        return index >= 0 ? index : ~index;
    }

    private bool IsRelocated(int offset) => Array.BinarySearch(relocations, offset) >= 0;

    private int ReadInt32(int offset) => BitConverter.ToInt32(FileBytes, offset);

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>
    /// The file offset that the 32-bit relative address at a file offset
    /// is of, or 0. The number is subtracted from the address.
    /// </summary>
    private int ResolveRelative(int offset, int subtracted = 0)
    {
        int rva = file.OffsetToRva(offset);
        return rva == 0 ? 0 : file.ResolveVirtualAddress(rva + ReadInt32(offset) - subtracted);
    }

    private static int IndexAt(List<Node> children, int offset)
    {
        int low = 0;
        int high = children.Count - 1;
        int result = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (children[middle].Start <= offset)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    private static bool IsGCStaticRegion(Node node) => node.Text != null && node.Text.StartsWith("NativeAOT GCStaticRegion", StringComparison.Ordinal);

    private void PlaceData(PEFile file)
    {
        // The section with the MethodTables, and each MethodTable with the
        // GC descriptor in front of it
        Section section = null;
        foreach (var candidate in file.Children.OfType<Section>())
        {
            if (candidate.HasChildren && candidate.Children.Any(c => c is MethodTable))
            {
                section = candidate;
                break;
            }
        }

        if (section == null)
        {
            DataMessage = "No MethodTables were found in the image, so no data nodes of the map file were placed.";
            return;
        }

        var children = section.Children;
        var units = new List<(int start, int end, MethodTable table)>();
        Node header = null;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is NativeAotModuleHeader)
            {
                header = children[i];
            }

            if (children[i] is not MethodTable table)
            {
                continue;
            }

            int start = table.Start;
            if (i > 0 && children[i - 1].End == start && children[i - 1] is not MethodTable && !IsGeneric(children[i - 1]) && children[i - 1].Text != null && children[i - 1].Text.StartsWith("GC descriptor", StringComparison.Ordinal))
            {
                start = children[i - 1].Start;
            }

            units.Add((start, table.End, table));
            methodTableStarts.Add(table.Start);
        }

        // The type nodes of the map file, matched in order by length
        var anchors = new List<int>();
        int unit = 0;
        int typeCount = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.Kind == HeaderKind && header != null && header.Length == node.Length)
            {
                node.Start = header.Start;
                anchors.Add(i);
                continue;
            }

            if (!node.Kind.EndsWith("EEType", StringComparison.Ordinal) || dataKinds.ContainsKey(node.Kind))
            {
                continue;
            }

            typeCount++;
            if (unit < units.Count && units[unit].end - units[unit].start == node.Length)
            {
                node.Start = units[unit].start;
                node.IsVerified = true;
                units[unit].table.Text = $"{units[unit].table.Text}: {node.Name}";
                anchors.Add(i);
                unit++;
            }
        }

        int matchedTypes = anchors.Count(a => nodes[a].Kind != HeaderKind);
        Details.Add($"types in the map file {typeCount:N0}, MethodTables in the image {units.Count:N0}, matched in order {matchedTypes:N0}");

        // The GC statics are read first: the entry of a type has the address
        // of its initial values, which are stored between the MethodTables
        var staticsSection = file.Children.OfType<Section>().FirstOrDefault(s => s.HasChildren && s.Children.Any(IsGCStaticRegion));
        var statics = new List<MapNode>();
        if (staticsSection != null)
        {
            Commit(section, ReadGCStatics(staticsSection, statics));
        }

        // The data nodes between two anchors are stored between them
        for (int a = 0; a + 1 < anchors.Count; a++)
        {
            int from = nodes[anchors[a]].Start + nodes[anchors[a]].Length;
            int to = nodes[anchors[a + 1]].Start;
            var candidates = new List<MapNode>();
            for (int i = anchors[a] + 1; i < anchors[a + 1]; i++)
            {
                var node = nodes[i];
                if (!dataKinds.ContainsKey(node.Kind))
                {
                    continue;
                }

                if (node.Start < 0)
                {
                    candidates.Add(node);
                }
                else if (node.Start >= from && node.Start + node.Length <= to)
                {
                    // A node that was found by an address: the nodes before
                    // it in the map file are stored before it
                    PlaceRange(section, from, node.Start, candidates, defaultAlignment: 8);
                    candidates = new List<MapNode>();
                    from = node.Start + node.Length;
                }
            }

            PlaceRange(section, from, to, candidates, defaultAlignment: 8);
        }

        CheckGCStaticsTypes();
        PlaceCompositions(section);
        int firstExceptionInfo = PlaceMethodData(section);

        // The generic variance bytes are stored after the generic
        // compositions, in the order of the map file, and the exception
        // handling info of the methods after them. They are sequences of
        // bytes and have no alignment.
        var lastComposition = children.LastOrDefault(c => Inspector.Kind(c) == CompositionText);
        var firstDispatchMap = children.FirstOrDefault(c => Inspector.Kind(c) == "'Interface dispatch map'");
        if (lastComposition != null && firstDispatchMap != null && firstDispatchMap.Start > lastComposition.End)
        {
            int to = firstExceptionInfo > lastComposition.End && firstExceptionInfo < firstDispatchMap.Start ? firstExceptionInfo : firstDispatchMap.Start;
            var candidates = nodes.Where(n => byteSequenceKinds.ContainsKey(n.Kind)).ToList();
            PlaceRange(section, lastComposition.End, to, candidates, defaultAlignment: 1);
        }

        if (staticsSection != null)
        {
            PlaceStatics(staticsSection, statics);
        }

        foreach (var (kind, entry) in placedByKind.OrderByDescending(p => p.Value.bytes))
        {
            Details.Add($"placed {kind}: {entry.count:N0} nodes, {entry.bytes:N0} bytes; {entry.verified:N0} nodes have the hash, {entry.addressed:N0} are found by an address");
        }

        DataMessage = $"Map file: {matchedTypes:N0} of its {typeCount:N0} types were matched to the {units.Count:N0} MethodTables of the image and named. " +
            $"{placedCount:N0} data nodes ({placedBytes:N0} bytes) of the map file were placed in the image: {verifiedBytes:N0} of these bytes have the hash that the map file lists, " +
            $"and {addressedBytes:N0} are found by an address that is stored in the image.";
    }

    private long placedBytes;
    private long verifiedBytes;
    private long addressedBytes;
    private int placedCount;
    private readonly Dictionary<string, (int count, long bytes, int verified, int addressed)> placedByKind = new();

    private static string GetLabel(string kind) =>
        dataKinds.TryGetValue(kind, out string label) || byteSequenceKinds.TryGetValue(kind, out label) || addressedKinds.TryGetValue(kind, out label) ? label : kind;

    private void Count(MapNode node)
    {
        placedCount++;
        placedBytes += node.Length;
        verifiedBytes += node.IsVerified ? node.Length : 0;
        addressedBytes += node.IsAddressed ? node.Length : 0;
        placedByKind.TryGetValue(node.Kind, out var entry);
        placedByKind[node.Kind] = (entry.count + 1, entry.bytes + node.Length, entry.verified + (node.IsVerified ? 1 : 0), entry.addressed + (node.IsAddressed ? 1 : 0));
    }

    private static void Reset(MapNode node)
    {
        node.Start = -1;
        node.IsVerified = false;
        node.IsAddressed = false;
    }

    /// <summary>
    /// Puts nodes for the placed nodes into the tree. A node that is over
    /// bytes with a label, or over another placed node, is not kept.
    /// </summary>
    private void Commit(Section section, List<MapNode> placed)
    {
        var children = section.Children;
        var kept = new List<MapNode>();
        int end = section.Start;
        int dropped = 0;
        foreach (var node in placed.Where(p => p.Start >= 0 && p.Length > 0).OrderBy(p => p.Start).ToList())
        {
            bool isFree = node.Start >= end && node.Start + node.Length <= section.End;
            int index = isFree ? IndexAt(children, node.Start) : -1;
            isFree = index >= 0;
            for (int i = index; isFree && i < children.Count && children[i].Start < node.Start + node.Length; i++)
            {
                if (!IsGeneric(children[i]) && children[i].End > node.Start)
                {
                    isFree = false;
                }
            }

            if (!isFree)
            {
                Reset(node);
                dropped++;
                continue;
            }

            kept.Add(node);
            end = node.Start + node.Length;
        }

        if (dropped > 0)
        {
            Details.Add($"{dropped:N0} nodes were not put into the tree: the bytes at their places have a label already");
        }

        if (kept.Count == 0)
        {
            return;
        }

        int from = children[IndexAt(children, kept[0].Start)].Start;
        int to = children[IndexAt(children, end - 1)].End;
        if (!ReplaceNodes(section, from, to, kept))
        {
            foreach (var node in kept)
            {
                Reset(node);
            }

            Details.Add($"{kept.Count:N0} nodes were not put into the tree: the range 0x{from:X}..0x{to:X} could not be replaced");
            return;
        }

        foreach (var node in kept)
        {
            Count(node);
        }
    }

    /// <summary>
    /// The GC static region of the image is a list of 32-bit relative
    /// addresses, one for the GC statics of each type. The statics of a
    /// type are two 32-bit relative addresses: of the MethodTable that
    /// describes the statics, with flags in the two lowest bits, and of
    /// the initial values, when the second flag is set. The entries are
    /// added to the list. The nodes of the initial values are returned.
    /// </summary>
    private List<MapNode> ReadGCStatics(Section statics, List<MapNode> placed)
    {
        const int EntryLength = 8;
        const int HasInitialValues = 2;
        var initialValues = new List<MapNode>();
        var region = statics.Children.First(IsGCStaticRegion);
        var entries = nodes.Where(n => n.Kind == GCStaticsKind).ToList();
        var targets = new SortedSet<int>();
        for (int offset = region.Start; offset + 4 <= region.End; offset += 4)
        {
            targets.Add(offset + ReadInt32(offset));
        }

        if (entries.Count == 0 || targets.Count != entries.Count || entries.Any(e => e.Length != EntryLength) ||
            targets.Min < statics.Start || targets.Max + EntryLength > statics.End)
        {
            Details.Add($"GC statics: the region of the image lists {targets.Count:N0} places and the map file has {entries.Count:N0} nodes, so none was placed");
            return initialValues;
        }

        var valuesByName = new Dictionary<string, MapNode>();
        foreach (var node in nodes)
        {
            if (node.Kind == PreInitDataKind)
            {
                valuesByName.TryAdd(node.Name, node);
            }
        }

        // The entries are stored in the order of the map file
        int index = 0;
        int withValues = 0;
        var before = new byte[EntryLength];
        foreach (int start in targets)
        {
            var node = entries[index++];
            node.Start = start;
            node.IsAddressed = true;
            placed.Add(node);

            // Before relocation the entry has the flags and no addresses
            int flags = ReadInt32(start) & 3;
            before[0] = (byte)flags;
            node.IsVerified = HasHash(node, before);
            gcStaticsTypes.Add(ResolveRelative(start, flags));

            if ((flags & HasInitialValues) == 0)
            {
                continue;
            }

            withValues++;
            int values = ResolveRelative(start + 4);
            if (values > 0 && valuesByName.TryGetValue(node.Name + "__PreInitData", out var data) && data.Start < 0)
            {
                data.Start = values;
                data.IsAddressed = true;
                data.IsVerified = Compare(data, values, withRelocations: true) != NoMatch;
                initialValues.Add(data);
            }
        }

        Details.Add($"GC statics: {entries.Count:N0} entries at 0x{targets.Min:X}..0x{targets.Max + EntryLength:X}; " +
            $"{withValues:N0} have the address of initial values, and for {initialValues.Count:N0} of these the map file has a node with the name of the type");
        return initialValues;
    }

    /// <summary>
    /// The first address of each entry of the GC statics.
    /// </summary>
    private readonly List<int> gcStaticsTypes = new();

    /// <summary>
    /// The first address of an entry of the GC statics is of a MethodTable
    /// for data only, which describes where the statics have references.
    /// </summary>
    private void CheckGCStaticsTypes()
    {
        if (gcStaticsTypes.Count == 0)
        {
            return;
        }

        var tables = nodes.Where(n => n.Kind == "DataOnlyEEType" && n.Start >= 0).OrderBy(n => n.Start).ToList();
        var starts = tables.Select(t => t.Start).ToArray();
        int count = 0;
        foreach (int target in gcStaticsTypes)
        {
            int index = Array.BinarySearch(starts, target);
            index = index >= 0 ? index : ~index - 1;
            if (methodTableStarts.Contains(target) || (index >= 0 && target < tables[index].Start + tables[index].Length))
            {
                count++;
            }
        }

        Details.Add($"GC statics: the first address of {count:N0} of the {gcStaticsTypes.Count:N0} entries is of a MethodTable");
    }

    /// <summary>
    /// The generic compositions are stored one after another in the order
    /// of the map file. The reader of the image finds the ones that a
    /// MethodTable has the address of. These are matched to the nodes of
    /// the map file by place and length, and named. The other nodes of
    /// the map file are placed before and between them.
    /// </summary>
    private void PlaceCompositions(Section section)
    {
        const int Alignment = 4;
        var children = section.Children;
        var found = children.Where(c => Inspector.Kind(c) == CompositionText).ToList();
        var listed = nodes.Where(n => n.Kind == CompositionKind && n.Length > 0).ToList();
        if (found.Count == 0 || listed.Count < found.Count)
        {
            return;
        }

        // The first one is stored after the last node with a label before
        // the ones that were found
        int first = children.IndexOf(found[0]);
        int before = first - 1;
        while (before >= 0 && IsGeneric(children[before]))
        {
            before--;
        }

        if (before < 0)
        {
            return;
        }

        int cursor = children[before].End;
        int next = 0;
        var placed = new List<MapNode>();
        var names = new List<string>();
        foreach (var node in listed)
        {
            int start = Align(cursor, Alignment);
            if (next < found.Count && found[next].Start == start && found[next].Length == node.Length)
            {
                names.Add(node.Name);
                next++;
            }
            else if (next < found.Count && start + node.Length <= found[next].Start)
            {
                node.Start = start;
                placed.Add(node);
            }
            else
            {
                break;
            }

            cursor = start + node.Length;
        }

        if (next < found.Count || names.Count + placed.Count != listed.Count)
        {
            foreach (var node in placed)
            {
                Reset(node);
            }

            Details.Add($"generic compositions: the map file has {listed.Count:N0} and {found.Count:N0} were found in the image; they are not stored one after another in the order of the map file, so none was named");
            return;
        }

        for (int i = 0; i < found.Count; i++)
        {
            found[i].Text = $"{found[i].Text}: {names[i]}";
        }

        Commit(section, placed);
        Details.Add($"generic compositions: the map file has {listed.Count:N0}; {found.Count:N0} were found in the image and named, and {placed.Count(p => p.Start >= 0):N0} more were placed before and between them");
    }

    /// <summary>
    /// The unwind data of a method has the addresses of its associated
    /// data and of its exception handling info. The linker stores nodes
    /// with the same bytes once. Returns the file offset of the first
    /// exception handling info, or -1.
    /// </summary>
    private int PlaceMethodData(Section section)
    {
        var table = file.Children.OfType<Section>().Where(s => s.HasChildren).SelectMany(s => s.Children).OfType<ExceptionTable>().FirstOrDefault();
        if (table == null || file.Machine != PEFile.MachineAmd64)
        {
            return -1;
        }

        // An entry of the exception table: the start and the end of a
        // function and the address of its unwind info
        const int EntryLength = 12;
        var unwindInfos = new Dictionary<int, int>();
        for (int entry = table.Start; entry + EntryLength <= table.End; entry += EntryLength)
        {
            int begin = file.ResolveVirtualAddress(ReadInt32(entry));
            int unwindInfo = file.ResolveVirtualAddress(ReadInt32(entry + 8));
            if (begin > 0 && unwindInfo > 0)
            {
                unwindInfos[begin] = unwindInfo;
            }
        }

        const int IsChained = 0x20;
        const int HasExceptionInfo = 0x04;
        const int HasAssociatedData = 0x10;
        var byStart = new Dictionary<int, MapNode>();
        int expectedInfos = 0;
        int foundInfos = 0;
        int expectedData = 0;
        int foundData = 0;

        void Place(MapNode node, int start, ref int found)
        {
            if (start < section.Start || start + node.Length > section.End)
            {
                return;
            }

            found++;
            if (byStart.TryGetValue(start, out var first))
            {
                first.SharedCount++;
                return;
            }

            node.Start = start;
            node.IsAddressed = true;
            node.IsVerified = Compare(node, start, withRelocations: true) != NoMatch;
            byStart[start] = node;
        }

        foreach (var method in methods)
        {
            var info = method.ExceptionInfo?.Length > 0 ? method.ExceptionInfo : null;
            var data = method.AssociatedData?.Length > 0 ? method.AssociatedData : null;
            if ((info == null && data == null) || method.Start < 0)
            {
                continue;
            }

            expectedInfos += info != null ? 1 : 0;
            expectedData += data != null ? 1 : 0;
            if (!unwindInfos.TryGetValue(method.Start, out int unwind) || unwind + 4 > FileBytes.Length || (FileBytes[unwind] & IsChained) != 0)
            {
                continue;
            }

            // The header of the unwind info and its codes, then the flags
            // of the method, the address of the associated data and the
            // address of the exception handling info
            int position = unwind + 4 + 2 * FileBytes[unwind + 2];
            if (position + 9 > FileBytes.Length)
            {
                continue;
            }

            int flags = FileBytes[position];
            position++;
            if ((flags & HasAssociatedData) != 0)
            {
                if (data != null)
                {
                    Place(data, file.ResolveVirtualAddress(ReadInt32(position)), ref foundData);
                }

                position += 4;
            }

            if ((flags & HasExceptionInfo) != 0 && info != null)
            {
                Place(info, file.ResolveVirtualAddress(ReadInt32(position)), ref foundInfos);
            }
        }

        var placed = byStart.Values.ToList();
        Commit(section, placed);
        Details.Add($"exception handling info: {expectedInfos:N0} methods have it in the map file; the unwind data of {foundInfos:N0} of them has its address, " +
            $"and the addresses are of {placed.Count(p => p.Kind == ExceptionInfoKind):N0} places");
        Details.Add($"associated data: {expectedData:N0} methods have it in the map file; the unwind data of {foundData:N0} of them has its address, " +
            $"and the addresses are of {placed.Count(p => p.Kind == AssociatedDataKind):N0} places");
        var kept = placed.Where(p => p.Start >= 0 && p.Kind == ExceptionInfoKind).ToList();
        return kept.Count > 0 ? kept.Min(k => k.Start) : -1;
    }

    /// <summary>
    /// The section with the statics has, after the frozen objects: the
    /// fixups of the P/Invoke methods, the non-GC statics and the GC
    /// statics. The list has the GC statics already.
    /// </summary>
    private void PlaceStatics(Section statics, List<MapNode> placed)
    {
        int size = file.PointerSize;
        int firstGCStatics = placed.Count > 0 ? placed.Min(p => p.Start) : -1;
        var frozen = statics.Children.OfType<FrozenObjectRegion>().FirstOrDefault();
        int cursor = frozen?.End ?? -1;

        // A fixup of a method: the address of the function, which is zero
        // in the file, the addresses of the name and of the fixup of the
        // module, and flags. The fixups are stored one after another.
        var methodFixups = nodes.Where(n => n.Kind == MethodFixupKind).ToList();
        var moduleTargets = new SortedSet<int>();
        int fixupCount = 0;
        foreach (var node in methodFixups)
        {
            if (cursor < 0)
            {
                break;
            }

            int start = Align(cursor, size);
            if (node.Length < 3 * size || start + node.Length > statics.End || !IsRelocated(start + size) || !IsRelocated(start + 2 * size) ||
                Compare(node, start, withRelocations: true) == NoMatch)
            {
                break;
            }

            node.Start = start;
            node.IsVerified = true;
            placed.Add(node);
            fixupCount++;
            cursor = start + node.Length;
            int module = file.ResolvePointerAt(start + 2 * size);
            if (module > 0)
            {
                moduleTargets.Add(module);
            }
        }

        // The fixups of the modules are stored in the order of the map file
        var moduleFixups = nodes.Where(n => n.Kind == ModuleFixupKind).ToList();
        if (moduleTargets.Count == moduleFixups.Count)
        {
            int index = 0;
            foreach (int start in moduleTargets)
            {
                var node = moduleFixups[index++];
                node.Start = start;
                node.IsAddressed = true;
                node.IsVerified = Compare(node, start, withRelocations: true) != NoMatch;
                placed.Add(node);
            }
        }

        Details.Add($"P/Invoke fixups: {fixupCount:N0} of {methodFixups.Count:N0} fixups of methods are stored after the frozen objects; they have the addresses of {moduleTargets.Count:N0} fixups of modules, and the map file has {moduleFixups.Count:N0}");

        if (fixupCount == methodFixups.Count && firstGCStatics > cursor)
        {
            PlaceNonGCStatics(cursor, firstGCStatics, placed);
        }

        Commit(statics, placed);
    }

    /// <summary>
    /// The non-GC statics of a type with a static constructor start with
    /// the address of the constructor, and the names of the two nodes have
    /// the name of the type: these nodes are found by that address. The
    /// statics of a type without bytes to store are not in the file. The
    /// nodes between two found ones are placed when their bytes have the
    /// hash, or when all of them fill the space in between.
    /// </summary>
    private void PlaceNonGCStatics(int from, int to, List<MapNode> placed)
    {
        const string ConstructorSuffix = "___cctor";
        var candidates = nodes.Where(n => n.Kind == NonGCStaticsKind && n.Length > 0).ToList();
        var indexByName = new Dictionary<string, int>();
        for (int i = 0; i < candidates.Count; i++)
        {
            indexByName.TryAdd(candidates[i].Name, i);
        }

        var methodNames = new Dictionary<int, string>();
        foreach (var method in methods)
        {
            if (method.Start >= 0)
            {
                methodNames[method.Start] = method.Name;
            }
        }

        // The constructor of a type with shared generic code is called
        // through a fat function pointer: the address is of the pointer,
        // plus two, and the node of the pointer has the name of the method
        const string FatPointerPrefix = "__fatpointer_";
        const int FatPointerOffset = 2;
        foreach (var node in nodes)
        {
            if (node.Kind == "FatFunctionPointer" && node.Start >= 0 && node.Name.StartsWith(FatPointerPrefix, StringComparison.Ordinal))
            {
                methodNames.TryAdd(node.Start + FatPointerOffset, node.Name.Substring(FatPointerPrefix.Length));
            }
        }

        var anchors = new List<(int start, int index)>();
        int lastIndex = -1;
        int end = from;
        int outOfOrder = 0;
        for (int i = FirstRelocation(from); i < relocations.Length && relocations[i] < to; i++)
        {
            int start = relocations[i];
            if (start < end || !methodNames.TryGetValue(file.ResolvePointerAt(start), out string name) || !name.EndsWith(ConstructorSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string type = name.Substring(0, name.Length - ConstructorSuffix.Length);
            if (!indexByName.TryGetValue($"?__NONGCSTATICS@{type}@@", out int index) || start + candidates[index].Length > to)
            {
                continue;
            }

            // The nodes are stored in the order of the map file
            if (index <= lastIndex)
            {
                outOfOrder++;
                continue;
            }

            anchors.Add((start, index));
            lastIndex = index;
            end = start + candidates[index].Length;
        }

        int before = placed.Count;
        int gapStart = from;
        int nextIndex = 0;
        for (int a = 0; a <= anchors.Count; a++)
        {
            int gapEnd = a < anchors.Count ? anchors[a].start : to;
            int endIndex = a < anchors.Count ? anchors[a].index : candidates.Count;
            PlaceBetween(candidates, nextIndex, endIndex, gapStart, gapEnd, placed);
            if (a == anchors.Count)
            {
                break;
            }

            var node = candidates[anchors[a].index];
            node.Start = anchors[a].start;
            node.IsAddressed = true;
            node.IsVerified = Compare(node, node.Start, withRelocations: true) != NoMatch;
            placed.Add(node);
            gapStart = node.Start + node.Length;
            nextIndex = anchors[a].index + 1;
        }

        int between = placed.Count - before - anchors.Count;
        Details.Add($"non-GC statics at 0x{from:X}..0x{to:X} ({to - from:N0} bytes): {candidates.Count:N0} nodes with bytes in the map file; {anchors.Count:N0} start with the address of the static constructor of their type, " +
            $"{between:N0} more were placed between these, {outOfOrder:N0} addresses of constructors are not in the order of the map file");
    }

    private readonly Dictionary<int, string> zeroHashes = new();

    /// <summary>
    /// The bytes of the node are zero before relocation.
    /// </summary>
    private bool IsZero(MapNode node)
    {
        if (!zeroHashes.TryGetValue(node.Length, out string hash))
        {
            hash = Convert.ToHexString(SHA256.HashData(new byte[node.Length]));
            zeroHashes[node.Length] = hash;
        }

        return string.Equals(hash, node.Hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Places nodes of the list in a file range. A node with bytes that
    /// are not zero is looked for by its hash. The other nodes are placed
    /// only when all of them fill the space up to the next found node.
    /// </summary>
    private void PlaceBetween(List<MapNode> candidates, int fromIndex, int toIndex, int from, int to, List<MapNode> placed)
    {
        int cursor = from;
        int index = fromIndex;
        for (int i = fromIndex; i < toIndex; i++)
        {
            var node = candidates[i];
            if (IsZero(node))
            {
                continue;
            }

            for (int position = cursor; position + node.Length <= to; position++)
            {
                if (Compare(node, position, withRelocations: true) == HashMatch)
                {
                    PlaceAll(candidates, index, i, cursor, position, placed);
                    node.Start = position;
                    node.IsVerified = true;
                    placed.Add(node);
                    cursor = position + node.Length;
                    index = i + 1;
                    break;
                }
            }
        }

        PlaceAll(candidates, index, toIndex, cursor, to, placed);
    }

    /// <summary>
    /// Places the nodes one after another when every one is consistent
    /// with the bytes at its place and the last one ends at the end of the
    /// range. When the nodes do not fill the range, some of them are not
    /// stored in the file, and which ones is not known.
    /// </summary>
    private void PlaceAll(List<MapNode> candidates, int fromIndex, int toIndex, int from, int to, List<MapNode> placed)
    {
        if (fromIndex >= toIndex || to <= from)
        {
            return;
        }

        int size = file.PointerSize;
        var starts = new int[toIndex - fromIndex];
        int cursor = from;
        for (int i = fromIndex; i < toIndex; i++)
        {
            // The alignment is not listed: the one of the largest field
            // that the length allows is taken
            var node = candidates[i];
            int start = Align(cursor, Math.Min(size, node.Length & -node.Length));
            if (start + node.Length > to || Compare(node, start, withRelocations: true) == NoMatch)
            {
                return;
            }

            starts[i - fromIndex] = start;
            cursor = start + node.Length;
        }

        if (to - cursor >= size)
        {
            return;
        }

        for (int i = fromIndex; i < toIndex; i++)
        {
            var node = candidates[i];
            node.Start = starts[i - fromIndex];
            node.IsVerified = true;
            placed.Add(node);
        }
    }

    /// <summary>
    /// Places the nodes in the file range and puts nodes for them into the tree.
    /// </summary>
    private void PlaceRange(Section section, int from, int to, List<MapNode> candidates, int defaultAlignment)
    {
        if (candidates.Count == 0 || to <= from)
        {
            return;
        }

        // Nodes of the range that the reader of the image has labelled already
        var obstacles = section.Children.Where(c => c.Start >= from && c.End <= to && !IsGeneric(c)).Select(c => (c.Start, c.End)).OrderBy(o => o.Start).ToList();
        bool fits = TryLayout(candidates, from, to, obstacles, defaultAlignment, out int end, out int verified);

        string kinds = string.Join(", ", candidates.GroupBy(c => c.Kind).Select(g => $"{g.Key} x{g.Count():N0} {g.Sum(c => (long)c.Length):N0}"));
        int lastKept = candidates.FindLastIndex(c => c.Start >= 0);
        string result = fits
            ? "all nodes fit"
            : lastKept < 0 ? "no node was placed" : $"placed up to node {lastKept + 1:N0}, the last one with a matching hash, at 0x{candidates[lastKept].Start:X}";
        Details.Add($"range 0x{from:X}..0x{to:X} ({to - from:N0} bytes): {kinds}; {to - end:N0} bytes left after the layout, {verified:N0} of {candidates.Count:N0} nodes have the hash; {result}");

        var kept = candidates.Where(c => c.Start >= 0 && c.Length > 0).ToList();
        if (kept.Count == 0 || !ReplaceNodes(section, from, to, kept))
        {
            foreach (var node in candidates)
            {
                Reset(node);
            }

            return;
        }

        foreach (var node in kept)
        {
            Count(node);
        }
    }

    /// <summary>
    /// Places the nodes one after another from the start of the range. A
    /// node starts at the next address that is a multiple of its
    /// alignment, which the map file does not list: the alignment for
    /// which the bytes have the hash of the node is taken, and the default
    /// alignment when none has. The layout fits when it ends less than 64
    /// bytes before the end of the range. When it does not fit, the nodes
    /// up to the last one with a matching hash keep their places.
    /// </summary>
    private bool TryLayout(List<MapNode> candidates, int from, int to, List<(int Start, int End)> obstacles, int defaultAlignment, out int end, out int verified)
    {
        const int MaximumSlack = 64;
        int[] alignments = [1, 2, 4, 8, 16, 32, 64];

        verified = 0;
        int cursor = from;
        int obstacle = 0;
        bool isComplete = true;
        bool isShared = defaultAlignment == 1;
        var seen = new HashSet<string>();
        var pending = new List<MapNode>();
        end = from;
        foreach (var node in candidates)
        {
            node.Start = -1;
            node.IsVerified = false;

            // A node is not placed over bytes that have a label already
            while (obstacle < obstacles.Count && obstacles[obstacle].End <= cursor)
            {
                obstacle++;
            }

            while (obstacle < obstacles.Count && obstacles[obstacle].Start < (cursor + defaultAlignment - 1) / defaultAlignment * defaultAlignment + node.Length)
            {
                cursor = obstacles[obstacle].End;
                obstacle++;
            }

            int start = -1;
            int previous = -1;
            foreach (int alignment in alignments)
            {
                int position = (cursor + alignment - 1) / alignment * alignment;
                if (position == previous)
                {
                    continue;
                }

                previous = position;
                if (position + node.Length > to)
                {
                    break;
                }

                if (Compare(node, position, withRelocations: true) == HashMatch)
                {
                    start = position;
                    node.IsVerified = true;
                    verified++;
                    break;
                }
            }

            // Data without addresses in it has to have the hash. When it
            // does not have it here, nodes of another section or bytes that
            // the map file does not list are in between: the place with the
            // hash is looked for further on.
            bool isJump = false;
            if (start < 0 && node.Length >= MinimumCheckedLength && plainDataKinds.Contains(node.Kind))
            {
                int window = Math.Clamp(SearchBudget / node.Length, MinimumSearchWindow, MaximumSearchWindow);
                for (int position = cursor + 1; position <= cursor + window && position + node.Length <= to; position++)
                {
                    if (HasHash(node, position))
                    {
                        start = position;
                        node.IsVerified = true;
                        verified++;
                        isJump = true;
                        break;
                    }
                }
            }

            // Equal sequences of bytes are stored once: a node with the
            // hash of an earlier node that is not found here has no bytes
            // of its own
            if (start < 0 && isShared && node.Hash != null && seen.Contains(node.Hash))
            {
                continue;
            }

            bool isChecked = node.IsVerified && node.Length >= MinimumCheckedLength;
            if (start < 0)
            {
                start = (cursor + defaultAlignment - 1) / defaultAlignment * defaultAlignment;
                if (start + node.Length > to)
                {
                    isComplete = false;
                    break;
                }
            }

            if (isShared && node.IsVerified && node.Hash != null)
            {
                seen.Add(node.Hash);
            }

            // The nodes since the last checked one are between two checked
            // places and fill the space between them, or else their places
            // are not known
            if (isJump)
            {
                foreach (var dropped in pending)
                {
                    dropped.Start = -1;
                    dropped.IsVerified = false;
                }

                pending.Clear();
            }
            else if (isChecked)
            {
                pending.Clear();
            }

            node.Start = start;
            cursor = start + node.Length;
            if (!isChecked)
            {
                pending.Add(node);
            }

            // A node of addresses and zero bytes has the hash at every
            // place with addresses and zero bytes: that is counted, and it
            // is not a check of the place
            if (!node.IsVerified && Compare(node, start, withRelocations: true) == ZeroMatch)
            {
                node.IsVerified = true;
                verified++;
            }
        }

        end = cursor;
        int labelledAfter = 0;
        foreach (var (obstacleStart, obstacleEnd) in obstacles)
        {
            if (obstacleStart >= cursor)
            {
                labelledAfter += obstacleEnd - obstacleStart;
            }
        }

        // When the layout does not end at the end of the range, the nodes
        // after the last checked one are not kept, and neither is a node
        // that was not reached
        bool fits = isComplete && to - cursor - labelledAfter < MaximumSlack;
        if (!fits)
        {
            foreach (var dropped in pending)
            {
                dropped.Start = -1;
                dropped.IsVerified = false;
            }
        }

        return fits;
    }

    /// <summary>
    /// Puts the placed nodes, which are in the order of their places, into
    /// the range. The nodes of the range under them have to be padding,
    /// unknown bytes, pointer tables or strings: these are replaced, with
    /// padding or unknown bytes for the parts that no placed node covers.
    /// </summary>
    private bool ReplaceNodes(Section section, int from, int to, List<MapNode> placed)
    {
        var children = section.Children;
        int first = children.FindIndex(c => c.End > from);
        if (first < 0)
        {
            return false;
        }

        // The nodes of the range that have a label already are kept, and
        // so are the others that no placed node is over
        var entries = new List<(int start, Node existing, MapNode placed)>();
        int last = first;
        int next = 0;
        while (last < children.Count && children[last].Start < to)
        {
            var child = children[last];
            if (child.Start < from || child.End > to)
            {
                return false;
            }

            while (next < placed.Count && placed[next].Start + placed[next].Length <= child.Start)
            {
                next++;
            }

            bool isCovered = next < placed.Count && placed[next].Start < child.End;
            if (!IsGeneric(child) || !isCovered)
            {
                entries.Add((child.Start, child, null));
            }

            last++;
        }

        foreach (var node in placed)
        {
            entries.Add((node.Start, null, node));
        }

        entries.Sort((left, right) => left.start.CompareTo(right.start));
        var replacement = new List<Node>();
        int position = from;
        void Fill(int upTo)
        {
            if (upTo <= position)
            {
                return;
            }

            bool isZero = true;
            for (int i = position; i < upTo; i++)
            {
                if (FileBytes[i] != 0)
                {
                    isZero = false;
                    break;
                }
            }

            Node filler = isZero ? new Padding() : new Unknown();
            filler.Buffer = section.Buffer;
            filler.Start = position;
            filler.Length = upTo - position;
            filler.Parent = section;
            replacement.Add(filler);
        }

        foreach (var (start, existing, node) in entries)
        {
            if (start < position)
            {
                return false;
            }

            Fill(start);
            if (existing != null)
            {
                replacement.Add(existing);
                position = existing.End;
                continue;
            }

            replacement.Add(new Node
            {
                Buffer = section.Buffer,
                Start = node.Start,
                Length = node.Length,
                Parent = section,
                Text = node.SharedCount > 0
                    ? $"{GetLabel(node.Kind)}: {node.Name} (and of {node.SharedCount:N0} more with the same bytes)"
                    : $"{GetLabel(node.Kind)}: {node.Name}"
            });
            position = node.Start + node.Length;
        }

        Fill(to);
        children.RemoveRange(first, last - first);
        children.InsertRange(first, replacement);
        return true;
    }
}
