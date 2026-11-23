using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;
using GuiLabs.FileFormat.PE.Dotnet;

class Program
{
    const string Usage = @"PETree <file>                     Write the full tree with hex bytes to a .txt file next to the input.
PETree <file1> <file2>            Also diff two files.
PETree <dir|@list|files...>       One summary line per .exe, .dll and .pdb (same as --summary).

Views of one file (printed to the console, or to a file with --out <path>):
  --outline                       Tree with offset, length and node type on each line.
      --depth N                   Levels to print (default 2).
      --run N                     Collapse more than N same-kind siblings in a row (default 3).
      --under <text>              Start at the first node with this type name or text.
      --at <offset>               Start at the smallest node that contains this file offset.
  --stats [N]                     Bytes per node kind per section, N kinds per section (default 15).
  --unknowns [N]                  Totals of unlabelled bytes and the N largest ranges with a hex preview (default 20).
  --at <offset>                   Chain of nodes that contain a file offset.
  --rva <rva>                     Same, for a relative virtual address.
  --find <text> [N]               Nodes whose text contains <text> or whose type name equals it.
  --hex <offset> [length]         Hex dump of a file range (default 256 bytes).
  --text                          The full tree with hex bytes, as written by the default mode.
  --summary                       One line: size, unknown, code, padding and parsed byte counts.

Numbers are decimal, or hexadecimal with a 0x prefix.";

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var paths = new List<string>();
        var options = new Dictionary<string, List<string>>();
        string currentOption = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--"))
            {
                currentOption = arg;
                options[currentOption] = new List<string>();
            }
            else if (currentOption != null && AcceptsValue(currentOption, options[currentOption].Count))
            {
                options[currentOption].Add(arg);
            }
            else
            {
                currentOption = null;
                paths.Add(arg);
            }
        }

        var files = ExpandPaths(paths, out bool isSet);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("No input files found.");
            return 1;
        }

        TextWriter writer = Console.Out;
        StreamWriter fileWriter = null;
        if (options.TryGetValue("--out", out var outValues) && outValues.Count == 1)
        {
            fileWriter = new StreamWriter(outValues[0]);
            writer = fileWriter;
        }

        try
        {
            if (isSet || (options.ContainsKey("--summary") && files.Count > 0))
            {
                int failed = 0;
                foreach (var file in files)
                {
                    var line = Inspector.Summarize(file);
                    if (line.StartsWith("FAILED"))
                    {
                        failed++;
                    }

                    writer.WriteLine(line);
                }

                return failed == 0 ? 0 : 2;
            }

            bool hasView = options.Keys.Any(k => k != "--out");
            if (!hasView)
            {
                return WriteTextDump(files);
            }

            var root = Inspector.ReadFile(files[0]);
            return RunViews(root, options, writer);
        }
        finally
        {
            fileWriter?.Dispose();
        }
    }

    static bool AcceptsValue(string option, int valuesSoFar)
    {
        int max = option switch
        {
            "--depth" or "--run" or "--under" or "--at" or "--rva" or "--out" or "--stats" or "--unknowns" or "--analyze" => 1,
            "--find" or "--hex" => 2,
            _ => 0
        };
        return valuesSoFar < max;
    }

    static List<string> ExpandPaths(List<string> paths, out bool isSet)
    {
        isSet = paths.Count > 2;
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (path.StartsWith("@"))
            {
                isSet = true;
                foreach (var line in File.ReadAllLines(path.Substring(1)))
                {
                    var trimmed = line.Trim().Trim('"');
                    if (trimmed.Length > 0 && !trimmed.StartsWith("#") && File.Exists(trimmed))
                    {
                        files.Add(trimmed);
                    }
                }
            }
            else if (Directory.Exists(path))
            {
                isSet = true;
                files.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        return files;
    }

    static int WriteTextDump(List<string> files)
    {
        var filePath = files[0];
        var root = Inspector.ReadFile(filePath);

        // Keep <name>.pdb and <name>.dll dumps side by side without clobbering each other
        var textFile = root is Metadata ? filePath + ".txt" : Path.ChangeExtension(filePath, ".txt");

        var text = root.GetText();
        File.WriteAllText(textFile, text);

        if (files.Count == 2)
        {
            var root2 = Inspector.ReadFile(files[1]);
            var diff = Difference.Diff(root, root2);
        }

        return 0;
    }

    static int RunViews(Node root, Dictionary<string, List<string>> options, TextWriter writer)
    {
        string Value(string option, int index = 0) =>
            options.TryGetValue(option, out var values) && values.Count > index ? values[index] : null;

        int Number(string option, int defaultValue, int index = 0) =>
            Value(option, index) is { } text ? ParseNumber(text) : defaultValue;

        if (options.ContainsKey("--outline"))
        {
            Node start = root;
            if (Value("--under") is { } under)
            {
                start = Inspector.FindFirst(root, under);
                if (start == null)
                {
                    writer.WriteLine($"No node matches '{under}'");
                    return 1;
                }
            }
            else if (Value("--at") is { } at)
            {
                start = root.FindDeepest(ParseNumber(at)) ?? root;
            }

            Inspector.PrintOutline(start, writer, Number("--depth", 2), Number("--run", 3));
            return 0;
        }

        if (options.ContainsKey("--stats"))
        {
            Inspector.PrintStats(root, writer, Number("--stats", 15));
        }

        if (options.ContainsKey("--unknowns") || options.ContainsKey("--analyze"))
        {
            Inspector.PrintUnknowns(root, writer, Number(options.ContainsKey("--unknowns") ? "--unknowns" : "--analyze", 20));
        }

        if (options.ContainsKey("--at"))
        {
            Inspector.PrintChainAt(root, writer, Number("--at", 0));
        }

        if (options.ContainsKey("--rva"))
        {
            int rva = Number("--rva", 0);
            int offset = root is PEFile peFile ? peFile.ResolveVirtualAddress(rva) : 0;
            if (offset <= 0)
            {
                writer.WriteLine($"RVA 0x{rva:X} has no file offset");
            }
            else
            {
                Inspector.PrintChainAt(root, writer, offset);
            }
        }

        if (Value("--find") is { } find)
        {
            Inspector.PrintFind(root, writer, find, Number("--find", 40, index: 1));
        }

        if (options.ContainsKey("--hex"))
        {
            writer.Write(Inspector.HexDump(root.Buffer, Number("--hex", 0), Number("--hex", 256, index: 1)));
        }

        if (options.ContainsKey("--text"))
        {
            writer.Write(root.GetText());
        }

        if (options.ContainsKey("--summary"))
        {
            var totals = Inspector.CountCategories(root);
            writer.WriteLine(string.Join(", ", Enum.GetValues<Inspector.ByteCategory>().Select(c => $"{c} {totals[(int)c]:N0}")));
        }

        return 0;
    }

    static int ParseNumber(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt32(text.Substring(2), 16);
        }

        return int.Parse(text);
    }
}
