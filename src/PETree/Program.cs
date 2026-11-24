using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;
using GuiLabs.FileFormat.PE.Dotnet;
using GuiLabs.FileFormat.Wasm;

class Program
{
    const string Usage = @"PETree <file>                     Write the full tree with hex bytes to a .txt file next to the input.
PETree <file1> <file2>            Also diff two files.
PETree <dir|@list|files...>       One summary line per .exe, .dll, .wasm and .pdb (same as --summary).

A .wasm file is read as a WebAssembly module. A Webcil module (a .NET assembly of a
browser build) is read down to the metadata tables of the assembly.

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
  --map <file.map.xml>            NativeAOT image: compare the labels with the compiler's map file (IlcGenerateMapFile).
  --mstat <file.mstat> [N]        NativeAOT image: bytes per assembly from the compiler's mstat file
                                  (IlcGenerateMstatFile), and the N largest namespaces, types and methods
                                  (default 20). With --map, the methods, the types and other data of the map
                                  file are also found in the image: their nodes are named, and --brotli adds
                                  the compressed length of the code. --details lists the file ranges.
  --aot [N]                       WebAssembly module with Mono AOT code: bytes of code and data per assembly, and
                                  the N largest namespaces, types and methods (default 20). The names are read
                                  from the assemblies (name.dll or name.wasm) and the object files (name.dll.o)
                                  of the build, in the folder of the module and in its ""aot-in"" folder.
      --names <folder> ...        More folders with these files.
      --brotli [quality]          Also the Brotli-compressed length of the code and data of each assembly
                                  (quality 0 to 11, default 11; slower).
      --functions <path>          Write one line per function, with tabs between the columns.

PETree --downloads <manifest> [N]   Browser app: the published files with their Brotli-compressed lengths, by group,
                                  and the N largest (default 20). The manifest is staticwebassets.publish.json
                                  in the obj folder of the project.

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
            else if (currentOption != null && AcceptsValue(currentOption, options[currentOption].Count) &&
                (currentOption is not ("--aot" or "--brotli") || int.TryParse(arg, out _)))
            {
                options[currentOption].Add(arg);
            }
            else
            {
                currentOption = null;
                paths.Add(arg);
            }
        }

        // This view reads a manifest of the build and no binary file
        if (options.TryGetValue("--downloads", out var downloadValues) && downloadValues.Count > 0)
        {
            int rows = downloadValues.Count > 1 && int.TryParse(downloadValues[1], out int count) ? count : 20;
            using var output = options.TryGetValue("--out", out var paths2) && paths2.Count == 1 ? new StreamWriter(paths2[0]) : null;
            DownloadSummary.Print(downloadValues[0], rows, output ?? Console.Out);
            return 0;
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
            return RunViews(root, files[0], options, writer);
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
            "--depth" or "--run" or "--under" or "--at" or "--rva" or "--out" or "--stats" or "--unknowns" or "--analyze" or "--map" or "--functions" or "--aot" or "--brotli" => 1,
            "--find" or "--hex" or "--mstat" or "--downloads" => 2,
            "--names" => 16,
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
                        f.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) ||
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

    static int RunViews(Node root, string filePath, Dictionary<string, List<string>> options, TextWriter writer)
    {
        string Value(string option, int index = 0) =>
            options.TryGetValue(option, out var values) && values.Count > index ? values[index] : null;

        int Number(string option, int defaultValue, int index = 0) =>
            Value(option, index) is { } text ? ParseNumber(text) : defaultValue;

        // The names are read first, so that the other views show them too
        MonoAotNames aotNames = null;
        if (root is WasmFile wasmFile && wasmFile.MonoAotModules.Count > 0 &&
            (options.ContainsKey("--aot") || options.ContainsKey("--names") || options.ContainsKey("--functions")))
        {
            aotNames = MonoAotNames.Read(wasmFile, filePath, options.TryGetValue("--names", out var folders) ? folders : []);
        }

        if (Value("--mstat") is { } mstatPath)
        {
            int quality = options.ContainsKey("--brotli") ? Math.Clamp(Number("--brotli", 11), 0, 11) : -1;
            NativeAotSummary.Print(root, mstatPath, Value("--map"), Number("--mstat", 20, index: 1), quality, options.ContainsKey("--details"), writer);
        }

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

        // With --mstat the map file is an input of that report
        if (Value("--map") is { } mapPath && !options.ContainsKey("--mstat"))
        {
            MapComparison.Print(root, mapPath, writer);
        }

        if (options.ContainsKey("--aot"))
        {
            int quality = options.ContainsKey("--brotli") ? Math.Clamp(Number("--brotli", 11), 0, 11) : -1;
            MonoAotSummary.Print(root, aotNames, Number("--aot", 20), quality, writer);
        }

        if (Value("--functions") is { } functionsPath)
        {
            MonoAotSummary.WriteFunctionList(root, aotNames, functionsPath);
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
