using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;

/// <summary>
/// Compares the labels of a parsed NativeAOT image with the map file that the
/// compiler writes (IlcGenerateMapFile). The map file lists every blob of the
/// image with its kind, name and length, without addresses, so the comparison
/// is by totals: how many bytes of each kind the compiler emitted, and how
/// many bytes the tree has under the label for that kind.
/// </summary>
public static class MapComparison
{
    private class MapKind
    {
        public string Name;
        public long Bytes;
        public int Count;
        public List<int> Lengths = new();
        public List<string> Names = new();
    }

    private class Label
    {
        public string Name;
        public long Bytes;
        public int Count;
        public bool IsUsed;
    }

    /// <summary>
    /// Map kinds that correspond to node kinds of the tree. A map kind that
    /// starts with an asterisk is a suffix. The tree's code and unwind info
    /// totals include the native runtime code, which the map file does not list.
    /// </summary>
    private static readonly (string[] mapKinds, string[] labels)[] rules =
    [
        (["MethodCode", "ReadyToRunHelper", "UnboxingStub", "ReadyToRunGenericLookupFromType", "ReadyToRunGenericLookupFromDictionary", "TentativeMethod", "TentativeInstanceMethod", "AddressTakenMethod"], ["NativeCode"]),
        (["*EEType"], ["MethodTable", "'GC descriptor'"]),
        (["GCInfo"], ["Amd64UnwindInfo", "NativeAotUnwindData"]),
        (["ArrayOfFrozenObjects"], ["FrozenObjectRegion"]),
        (["InterfaceDispatchMap"], ["'Interface dispatch map'"]),
        (["SealedVTable"], ["'Sealed vtable'"]),
        (["GenericComposition"], ["'Generic composition'"]),
        (["WritableData"], ["'Writable data of a MethodTable'"]),
        (["TypeGenericDictionary"], ["'Type generic dictionary'"])
    ];

    public static void Print(Node root, string mapPath, TextWriter writer)
    {
        var mapKinds = ReadMap(mapPath);
        var labels = CollectLabels(root);

        var rows = new List<(string kind, int count, long bytes, string label, int labelCount, long labelBytes, bool isMatched)>();
        var usedMapKinds = new HashSet<string>();

        foreach (var (ruleKinds, labelNames) in rules)
        {
            string mapKind = ruleKinds.Length == 1 ? ruleKinds[0] : ruleKinds[0] + " and stubs";
            var matching = mapKinds.Values
                .Where(k => ruleKinds.Any(r => r.StartsWith("*") ? k.Name.EndsWith(r.Substring(1)) : k.Name == r))
                .ToArray();
            if (matching.Length == 0)
            {
                continue;
            }

            foreach (var kind in matching)
            {
                usedMapKinds.Add(kind.Name);
            }

            var found = labelNames.Where(labels.ContainsKey).Select(name => labels[name]).ToArray();
            foreach (var label in found)
            {
                label.IsUsed = true;
            }

            rows.Add((
                mapKind,
                matching.Sum(k => k.Count),
                matching.Sum(k => k.Bytes),
                string.Join(" + ", labelNames),
                found.Sum(l => l.Count),
                found.Sum(l => l.Bytes),
                found.Length > 0));
        }

        // A blob that the module header lists has the same length there
        var regions = CollectRegions(root);
        foreach (var kind in mapKinds.Values.Where(k => !usedMapKinds.Contains(k.Name) && k.Count <= 8).OrderByDescending(k => k.Bytes))
        {
            var matched = new List<(string name, long declared, long onFile, string label)>();
            for (int i = 0; i < kind.Lengths.Count; i++)
            {
                // Several rows can have the same length: prefer the one
                // whose name resembles the name or the kind in the map file
                int length = kind.Lengths[i];
                string blobName = Normalize(kind.Names[i]);
                string kindName = Normalize(kind.Name);
                int index = regions.FindIndex(r => r.declared == length && (blobName.Contains(Normalize(r.name)) || kindName.Contains(Normalize(r.name)) || Normalize(r.name).Contains(kindName)));
                if (index < 0)
                {
                    index = regions.FindIndex(r => r.declared == length);
                }

                if (index >= 0)
                {
                    matched.Add(regions[index]);
                    regions.RemoveAt(index);
                }
            }

            if (matched.Count != kind.Count)
            {
                // Put back partial matches: the kind is reported as not labelled
                regions.AddRange(matched);
                continue;
            }

            usedMapKinds.Add(kind.Name);
            foreach (var region in matched)
            {
                if (region.label != null && labels.TryGetValue(region.label, out var label))
                {
                    label.IsUsed = true;
                }
            }

            rows.Add((
                kind.Name,
                kind.Count,
                kind.Bytes,
                "NativeAOT " + string.Join(" + ", matched.Select(m => m.name)),
                matched.Count,
                matched.Sum(m => m.onFile),
                true));
        }

        foreach (var kind in mapKinds.Values.Where(k => !usedMapKinds.Contains(k.Name)))
        {
            rows.Add((kind.Name, kind.Count, kind.Bytes, "", 0, 0, false));
        }

        long mapTotal = mapKinds.Values.Sum(k => k.Bytes);
        writer.WriteLine($"{"Map kind",-42} {"count",8} {"bytes",12}    {"count",8} {"bytes",12} {"difference",11}  Label in the tree");
        foreach (var row in rows.OrderByDescending(r => r.isMatched).ThenByDescending(r => r.bytes))
        {
            if (row.isMatched)
            {
                writer.WriteLine($"{row.kind,-42} {row.count,8:N0} {row.bytes,12:N0}    {row.labelCount,8:N0} {row.labelBytes,12:N0} {row.labelBytes - row.bytes,11:N0}  {row.label}");
            }
            else
            {
                writer.WriteLine($"{row.kind,-42} {row.count,8:N0} {row.bytes,12:N0}    {"",8} {"",12} {"",11}  (no label)");
            }
        }

        long matchedBytes = rows.Where(r => r.isMatched).Sum(r => r.bytes);
        long unmatchedBytes = rows.Where(r => !r.isMatched).Sum(r => r.bytes);
        writer.WriteLine();
        writer.WriteLine($"Map file total:             {mapTotal,12:N0} bytes");
        writer.WriteLine($"  kinds with a label:       {matchedBytes,12:N0} ({100.0 * matchedBytes / Math.Max(mapTotal, 1):F1}%)");
        writer.WriteLine($"  kinds without a label:    {unmatchedBytes,12:N0} ({100.0 * unmatchedBytes / Math.Max(mapTotal, 1):F1}%)");
        writer.WriteLine();
        writer.WriteLine("Node kinds of the tree that no map kind was matched to:");
        foreach (var label in labels.Values.Where(l => !l.IsUsed).OrderByDescending(l => l.Bytes).Take(25))
        {
            writer.WriteLine($"  {label.Bytes,12:N0} {label.Count,8:N0} x  {label.Name}");
        }
    }

    private static Dictionary<string, MapKind> ReadMap(string mapPath)
    {
        var kinds = new Dictionary<string, MapKind>();
        using var reader = XmlReader.Create(mapPath);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
            {
                continue;
            }

            string lengthText = reader.GetAttribute("Length");
            if (lengthText == null || !int.TryParse(lengthText, out int length))
            {
                continue;
            }

            if (!kinds.TryGetValue(reader.Name, out var kind))
            {
                kind = new MapKind { Name = reader.Name };
                kinds[reader.Name] = kind;
            }

            kind.Bytes += length;
            kind.Count++;
            if (kind.Lengths.Count <= 8)
            {
                kind.Lengths.Add(length);
                kind.Names.Add(reader.GetAttribute("Name") ?? "");
            }
        }

        return kinds;
    }

    private static string Normalize(string name) => name.Replace("_", "").ToLowerInvariant();

    /// <summary>
    /// Bytes and node count per node kind, for the nodes directly under a section.
    /// </summary>
    private static Dictionary<string, Label> CollectLabels(Node root)
    {
        var labels = new Dictionary<string, Label>();
        if (!root.HasChildren)
        {
            return labels;
        }

        foreach (var section in root.Children.OfType<Section>())
        {
            if (!section.HasChildren)
            {
                continue;
            }

            foreach (var node in section.Children)
            {
                string kind = Inspector.Kind(node);
                if (!labels.TryGetValue(kind, out var label))
                {
                    label = new Label { Name = kind };
                    labels[kind] = label;
                }

                label.Bytes += node.Length;
                label.Count++;
            }
        }

        return labels;
    }

    /// <summary>
    /// The rows of the NativeAOT module header: name, declared length, and
    /// the bytes that the tree has under the label of that name.
    /// </summary>
    private static List<(string name, long declared, long onFile, string label)> CollectRegions(Node root)
    {
        var regions = new List<(string name, long declared, long onFile, string label)>();
        if (root is not PEFile peFile)
        {
            return regions;
        }

        foreach (var header in peFile.NativeAotModuleHeaders)
        {
            foreach (var row in header.Rows)
            {
                string name = ReadyToRunSection.GetSectionTypeName(row.ReadInt32(0));
                ulong start = peFile.ReadPointer(row.Children[2].Start);
                long declared = header.HasLengthField
                    ? row.ReadInt32(1)
                    : (long)(peFile.ReadPointer(row.Children[3].Start) - start);

                // The node that starts at the region's file offset, directly
                // under its section. A region can extend past the file data
                // of its section, so the bytes in the file can be fewer.
                long onFile = 0;
                string label = null;
                int offset = peFile.ResolvePointer(start);
                var node = offset > 0 ? peFile.FindDeepest(offset) : null;
                while (node != null && node.Parent is not Section)
                {
                    node = node.Parent;
                }

                if (node != null && node.Start == offset && node is not Unknown && node is not Padding)
                {
                    onFile = Math.Min(node.Length, declared);
                    label = Inspector.Kind(node);
                }

                regions.Add((name, declared, onFile, label));
            }
        }

        return regions;
    }
}
