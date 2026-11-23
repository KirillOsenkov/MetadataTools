using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;
using GuiLabs.FileFormat.PE.Dotnet;

/// <summary>
/// Text views of a parsed tree for use from the command line: an outline with
/// offsets, byte statistics per section, a list of unlabelled ranges, and
/// lookups by offset, RVA and text.
/// </summary>
public static class Inspector
{
    public static Node ReadFile(string filePath)
    {
        if (Metadata.IsMetadataFile(filePath))
        {
            return Metadata.ReadFromFile(filePath);
        }

        return PEFile.ReadFromFile(filePath);
    }

    public enum ByteCategory
    {
        Structured,
        Code,
        Padding,
        Unknown,
        UnparsedSection,

        /// <summary>
        /// A range whose purpose is known from a table of contents, with
        /// contents that are not decoded.
        /// </summary>
        Named
    }

    public static ByteCategory Categorize(Node leaf)
    {
        if (leaf is Unknown)
        {
            return ByteCategory.Unknown;
        }

        if (leaf is Region)
        {
            return ByteCategory.Named;
        }

        if (leaf is Padding)
        {
            return ByteCategory.Padding;
        }

        if (leaf is Section)
        {
            return ByteCategory.UnparsedSection;
        }

        if (leaf is NativeCode || (leaf.GetType() == typeof(Node) && leaf.Text != null && leaf.Text.StartsWith("Native code")))
        {
            return ByteCategory.Code;
        }

        return ByteCategory.Structured;
    }

    public static IEnumerable<Node> Leaves(Node node)
    {
        if (!node.HasChildren)
        {
            yield return node;
            yield break;
        }

        foreach (var child in node.Children)
        {
            foreach (var leaf in Leaves(child))
            {
                yield return leaf;
            }
        }
    }

    public static IEnumerable<Node> Flatten(Node node)
    {
        yield return node;
        if (node.HasChildren)
        {
            foreach (var child in node.Children)
            {
                foreach (var descendant in Flatten(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    public static long[] CountCategories(Node root)
    {
        var totals = new long[6];
        foreach (var leaf in Leaves(root))
        {
            totals[(int)Categorize(leaf)] += leaf.Length;
        }

        return totals;
    }

    /// <summary>
    /// One line per file: size, then bytes per category. Used to compare a set
    /// of files before and after a parser change.
    /// </summary>
    public static string Summarize(string filePath)
    {
        string name = filePath;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var root = ReadFile(filePath);
            var totals = CountCategories(root);
            long size = root.Buffer.Length;
            long unknown = totals[(int)ByteCategory.Unknown] + totals[(int)ByteCategory.UnparsedSection];
            double percent = size == 0 ? 0 : 100.0 * unknown / size;
            return $"{size,10} unknown {unknown,9} {percent,5:F1}%  named {totals[(int)ByteCategory.Named],9}  code {totals[(int)ByteCategory.Code],9}  pad {totals[(int)ByteCategory.Padding],8}  parsed {totals[(int)ByteCategory.Structured],9}  {stopwatch.ElapsedMilliseconds,5}ms  {name}";
        }
        catch (Exception ex)
        {
            string message = ex.Message.Replace("\r", "").Replace("\n", " | ");
            if (message.Length > 300)
            {
                message = message.Substring(0, 300);
            }

            return $"FAILED {ex.GetType().Name}: {message}  {name}";
        }
    }

    public static void PrintOutline(Node root, TextWriter writer, int maxDepth, int maxRun)
    {
        PrintOutline(root, writer, 0, maxDepth, maxRun);
    }

    private static void PrintOutline(Node node, TextWriter writer, int level, int maxDepth, int maxRun)
    {
        writer.WriteLine(new string(' ', level * 2) + Describe(node));
        if (!node.HasChildren || level >= maxDepth)
        {
            return;
        }

        var children = node.Children;
        int i = 0;
        while (i < children.Count)
        {
            string kind = Kind(children[i]);
            int j = i;
            while (j < children.Count && Kind(children[j]) == kind)
            {
                j++;
            }

            int count = j - i;
            if (count > maxRun)
            {
                PrintOutline(children[i], writer, level + 1, maxDepth, maxRun);
                long bytes = 0;
                for (int k = i + 1; k < j; k++)
                {
                    bytes += children[k].Length;
                }

                writer.WriteLine(new string(' ', (level + 1) * 2) + $"... {count - 1} more {kind}, {bytes:N0} bytes, through 0x{children[j - 1].End:X}");
            }
            else
            {
                for (int k = i; k < j; k++)
                {
                    PrintOutline(children[k], writer, level + 1, maxDepth, maxRun);
                }
            }

            i = j;
        }
    }

    /// <summary>
    /// The node kind used to group siblings: the class name, or for plain
    /// nodes the text up to the first digit or parenthesis.
    /// </summary>
    public static string Kind(Node node)
    {
        var type = node.GetType();
        if (type != typeof(Node) && type != typeof(BytesNode) && type != typeof(Record) && type != typeof(RecordTable) && type != typeof(Region))
        {
            return type.Name;
        }

        // Cut the text where the part that differs between siblings begins:
        // a parenthesis, a colon, or a number that starts a word
        string text = node.Text ?? "";
        int cut = -1;
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(' || c == ':' || (char.IsDigit(c) && (text[i - 1] == ' ' || text[i - 1] == '+')))
            {
                cut = i;
                break;
            }
        }

        if (cut > 0)
        {
            text = text.Substring(0, cut);
        }

        return "'" + text.Trim() + "'";
    }

    public static string Describe(Node node)
    {
        string text = node.Text ?? "";
        text = text.Replace("\r", "\\r").Replace("\n", "\\n");
        if (text.Length > 100)
        {
            text = text.Substring(0, 100) + "...";
        }

        string children = node.HasChildren ? $" [{node.Children.Count}]" : "";
        string typeName = node.GetType().Name;
        return $"0x{node.Start:X6} +{node.Length,-7} {typeName}: {text}{children}";
    }

    public static void PrintStats(Node root, TextWriter writer, int top)
    {
        var totals = new Dictionary<(string section, string kind), (int count, long bytes)>();
        CollectStats(root, "(outside sections)", totals);

        foreach (var group in totals.GroupBy(kv => kv.Key.section).OrderBy(g => g.Key))
        {
            long sectionBytes = group.Sum(kv => kv.Value.bytes);
            writer.WriteLine($"{group.Key}: {sectionBytes:N0} bytes");
            var ordered = group.OrderByDescending(kv => kv.Value.bytes).ToArray();
            foreach (var kv in ordered.Take(top))
            {
                writer.WriteLine($"  {kv.Value.bytes,12:N0} {100.0 * kv.Value.bytes / sectionBytes,5:F1}% {kv.Value.count,7} x  {kv.Key.kind}");
            }

            if (ordered.Length > top)
            {
                writer.WriteLine($"  {ordered.Skip(top).Sum(kv => kv.Value.bytes),12:N0}        {ordered.Length - top,7} more kinds");
            }
        }

        var categories = CountCategories(root);
        long size = root.Buffer.Length;
        writer.WriteLine($"TOTAL {size:N0} bytes: " + string.Join(", ", Enum.GetValues<ByteCategory>().Select(c => $"{c} {categories[(int)c]:N0} ({100.0 * categories[(int)c] / Math.Max(size, 1):F1}%)")));
    }

    private static void CollectStats(Node node, string section, Dictionary<(string, string), (int count, long bytes)> totals)
    {
        if (node is Section)
        {
            section = node.Text;
        }

        if (!node.HasChildren)
        {
            Node owner = node;
            while (owner.Parent != null && owner.Parent is not Section && owner.Parent.Parent != null)
            {
                owner = owner.Parent;
            }

            string kind;
            if (node is Section)
            {
                kind = "(unparsed section body)";
            }
            else if (node is Unknown && owner != node)
            {
                kind = $"Unknown inside {Kind(node.Parent)}";
            }
            else
            {
                kind = Kind(owner);
            }

            var key = (section, kind);
            totals.TryGetValue(key, out var value);
            totals[key] = (value.count + 1, value.bytes + node.Length);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectStats(child, section, totals);
        }
    }

    public static void PrintUnknowns(Node root, TextWriter writer, int top)
    {
        var unknowns = Leaves(root)
            .Where(n => Categorize(n) is ByteCategory.Unknown or ByteCategory.UnparsedSection)
            .ToArray();

        foreach (var group in unknowns.GroupBy(SectionName).OrderByDescending(g => g.Sum(n => (long)n.Length)))
        {
            writer.WriteLine($"{group.Key}: {group.Count()} ranges, {group.Sum(n => (long)n.Length):N0} bytes");
        }

        writer.WriteLine($"TOTAL: {unknowns.Length} ranges, {unknowns.Sum(n => (long)n.Length):N0} bytes");

        var peFile = root as PEFile;
        foreach (var node in unknowns.OrderByDescending(n => n.Length).Take(top))
        {
            string rva = peFile != null && TryGetRva(peFile, node.Start, out int value) ? $" rva 0x{value:X}" : "";
            string parent = node.Parent == null ? "" : Kind(node.Parent);
            writer.WriteLine($"0x{node.Start:X6} +{node.Length,-7}{rva} in {SectionName(node)} / {parent}");
            int previewLength = Math.Min(node.Length, 48);
            writer.Write(HexDump(root.Buffer, node.Start, previewLength, indent: "    "));
        }
    }

    public static string SectionName(Node node)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is Section)
            {
                return current.Text;
            }
        }

        return "(outside sections)";
    }

    public static bool TryGetRva(PEFile peFile, int offset, out int rva)
    {
        rva = 0;
        if (peFile.SectionTable == null)
        {
            return false;
        }

        foreach (var header in peFile.SectionTable.SectionHeaders)
        {
            int raw = header.PointerToRawData.Value;
            if (raw != 0 && offset >= raw && offset < raw + header.SizeOfRawData.Value)
            {
                rva = offset - raw + header.VirtualAddress.Value;
                return true;
            }
        }

        return false;
    }

    public static void PrintChainAt(Node root, TextWriter writer, int offset)
    {
        var found = root.FindDeepest(offset);
        if (found == null)
        {
            writer.WriteLine($"No node at 0x{offset:X}");
            return;
        }

        var chain = new List<Node>();
        for (var current = found; current != null; current = current.Parent)
        {
            chain.Add(current);
        }

        chain.Reverse();
        for (int i = 0; i < chain.Count; i++)
        {
            writer.WriteLine(new string(' ', i * 2) + Describe(chain[i]));
        }

        if (root is PEFile peFile && TryGetRva(peFile, offset, out int rva))
        {
            writer.WriteLine($"offset 0x{offset:X} = rva 0x{rva:X}");
        }
    }

    public static void PrintFind(Node root, TextWriter writer, string text, int top)
    {
        int count = 0;
        foreach (var node in Flatten(root))
        {
            bool matches =
                (node.Text != null && node.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
                node.GetType().Name.Equals(text, StringComparison.OrdinalIgnoreCase);
            if (!matches)
            {
                continue;
            }

            count++;
            if (count <= top)
            {
                writer.WriteLine(Describe(node) + "    <- " + (node.Parent == null ? "" : Kind(node.Parent)));
            }
        }

        writer.WriteLine($"{count} matches");
    }

    public static Node FindFirst(Node root, string text)
    {
        bool IsExact(Node node) =>
            node.GetType().Name.Equals(text, StringComparison.OrdinalIgnoreCase) ||
            (node.Text != null && node.Text.Equals(text, StringComparison.OrdinalIgnoreCase));

        return Flatten(root).FirstOrDefault(node => node.HasChildren && IsExact(node))
            ?? Flatten(root).FirstOrDefault(IsExact)
            ?? Flatten(root).FirstOrDefault(node => node.Text != null && node.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    public static string HexDump(ByteBuffer buffer, int start, int length, string indent = "")
    {
        var sb = new StringBuilder();
        long end = Math.Min((long)start + length, buffer.Length);
        for (long line = start; line < end; line += 16)
        {
            sb.Append(indent);
            sb.Append($"{line:X8}  ");
            var ascii = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                if (line + i < end)
                {
                    byte b = buffer.ReadByte((int)(line + i));
                    sb.Append(b.ToString("X2"));
                    sb.Append(i == 7 ? "  " : " ");
                    ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                else
                {
                    sb.Append(i == 7 ? "    " : "   ");
                }
            }

            sb.Append(' ');
            sb.Append(ascii);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
