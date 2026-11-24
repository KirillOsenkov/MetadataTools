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
/// its place is checked with the hash.
/// </summary>
internal sealed class NativeAotLayout
{
    private const string MethodCodeKind = "MethodCode";
    private const string HeaderKind = "ReadyToRunHeader";

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
        ["ManagedDataDescriptor"] = "Managed data descriptor"
    };

    /// <summary>
    /// The kinds that are stored after the generic compositions.
    /// </summary>
    private static readonly Dictionary<string, string> byteSequenceKinds = new()
    {
        ["GenericVariance"] = "Generic variance",
        ["MethodExceptionHandlingInfo"] = "Exception handling info of a method"
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
        public bool IsVerified;
    }

    private readonly List<MapNode> nodes = new();
    private readonly List<MapNode> methods = new();
    private readonly List<NativeCode> functions = new();

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

    private bool HasHash(MapNode node, int start)
    {
        if (node.Hash == null || node.Length == 0 || start < 0 || start + node.Length > FileBytes.Length)
        {
            return false;
        }

        string hash = Convert.ToHexString(SHA256.HashData(FileBytes.AsSpan(start, node.Length)));
        return string.Equals(hash, node.Hash, StringComparison.OrdinalIgnoreCase);
    }

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

            if (!node.Kind.EndsWith("EEType", StringComparison.Ordinal))
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

        // The data nodes between two anchors are stored between them
        for (int a = 0; a + 1 < anchors.Count; a++)
        {
            int from = nodes[anchors[a]].Start + nodes[anchors[a]].Length;
            int to = nodes[anchors[a + 1]].Start;
            var candidates = new List<MapNode>();
            for (int i = anchors[a] + 1; i < anchors[a + 1]; i++)
            {
                if (dataKinds.ContainsKey(nodes[i].Kind))
                {
                    candidates.Add(nodes[i]);
                }
            }

            PlaceRange(section, from, to, candidates, defaultAlignment: 8);
        }

        // The generic variance bytes and the exception handling info of the
        // methods are stored after the generic compositions and before the
        // interface dispatch maps, in the order of the map file. They are
        // sequences of bytes and have no alignment.
        var lastComposition = children.LastOrDefault(c => Inspector.Kind(c) == "'Generic composition'");
        var firstDispatchMap = children.FirstOrDefault(c => Inspector.Kind(c) == "'Interface dispatch map'");
        if (lastComposition != null && firstDispatchMap != null && firstDispatchMap.Start > lastComposition.End)
        {
            var candidates = nodes.Where(n => byteSequenceKinds.ContainsKey(n.Kind)).ToList();
            PlaceRange(section, lastComposition.End, firstDispatchMap.Start, candidates, defaultAlignment: 1);
        }

        foreach (var (kind, entry) in placedByKind.OrderByDescending(p => p.Value.bytes))
        {
            Details.Add($"placed {kind}: {entry.count:N0} nodes, {entry.bytes:N0} bytes, {entry.verified:N0} checked by hash");
        }

        DataMessage = $"Map file: {matchedTypes:N0} of its {typeCount:N0} types were matched to the {units.Count:N0} MethodTables of the image and named. " +
            $"{placedCount:N0} data nodes ({placedBytes:N0} bytes) were placed between them; {verifiedBytes:N0} of these bytes have the hash that the map file lists.";
    }

    private long placedBytes;
    private long verifiedBytes;
    private int placedCount;
    private readonly Dictionary<string, (int count, long bytes, int verified)> placedByKind = new();

    private static string GetLabel(string kind) => dataKinds.TryGetValue(kind, out string label) ? label : byteSequenceKinds[kind];

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
                node.Start = -1;
            }

            return;
        }

        foreach (var node in kept)
        {
            placedCount++;
            placedBytes += node.Length;
            verifiedBytes += node.IsVerified ? node.Length : 0;
            placedByKind.TryGetValue(node.Kind, out var entry);
            placedByKind[node.Kind] = (entry.count + 1, entry.bytes + node.Length, entry.verified + (node.IsVerified ? 1 : 0));
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

                if (HasHash(node, position))
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
    /// Replaces the nodes of the range, which have to be padding, unknown
    /// bytes, pointer tables or strings, with the placed nodes and with
    /// padding or unknown bytes between them.
    /// </summary>
    private bool ReplaceNodes(Section section, int from, int to, List<MapNode> placed)
    {
        var children = section.Children;
        int first = children.FindIndex(c => c.End > from);
        if (first < 0)
        {
            return false;
        }

        // The nodes of the range that have a label already are kept
        var entries = new List<(int start, Node existing, MapNode placed)>();
        int last = first;
        while (last < children.Count && children[last].Start < to)
        {
            if (children[last].Start < from || children[last].End > to)
            {
                return false;
            }

            if (!IsGeneric(children[last]))
            {
                entries.Add((children[last].Start, children[last], null));
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
                Text = $"{GetLabel(node.Kind)}: {node.Name}"
            });
            position = node.Start + node.Length;
        }

        Fill(to);
        children.RemoveRange(first, last - first);
        children.InsertRange(first, replacement);
        return true;
    }
}
