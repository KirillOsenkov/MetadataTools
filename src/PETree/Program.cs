using System.Collections.Generic;
using System.IO;
using System.Linq;
using GuiLabs.FileFormat;
using GuiLabs.FileFormat.PE;
using GuiLabs.Utilities;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        var filePath = args[0];
        if (Directory.Exists(filePath))
        {
            var dlls = Directory.GetFiles(filePath, "*.dll", SearchOption.AllDirectories);
            foreach (var dll in dlls)
            {
                try
                {
                    System.Console.WriteLine($"Reading {dll}");
                    var file = PEFile.ReadFromFile(dll);
                }
                catch (System.Exception ex)
                {
                    System.Console.WriteLine($"Exception when reading {dll}: {ex.ToString()}");
                }
            }

            return;
        }

        if (!File.Exists(filePath))
        {
            return;
        }

        var peFile = PEFile.ReadFromFile(filePath);

        if (args.Length >= 2 && args[1] == "--analyze")
        {
            AnalyzeUnknowns(peFile);
            return;
        }

        var textFile = Path.ChangeExtension(filePath, ".txt");

        var text = peFile.GetText();
        File.WriteAllText(textFile, text);

        if (args.Length == 2)
        {
            var filePath2 = args[1];
            if (!File.Exists(filePath2))
            {
                return;
            }

            var peFile2 = PEFile.ReadFromFile(filePath2);

            var diff = Difference.Diff(peFile, peFile2);
        }
    }

    static void AnalyzeUnknowns(PEFile peFile)
    {
        var unknowns = new List<(string section, int start, int length, byte[] sample)>();
        CollectUnknowns(peFile, null, unknowns);

        // Group by section
        var groups = unknowns.GroupBy(u => u.section ?? "(none)")
            .OrderByDescending(g => g.Sum(u => u.length));
        
        int totalCount = 0, totalBytes = 0;
        foreach (var g in groups)
        {
            int count = g.Count();
            int bytes = g.Sum(u => u.length);
            totalCount += count;
            totalBytes += bytes;
            
            // Categorize: pure CC, pure zero, mixed
            int ccCount = 0, zeroCount = 0, mixedCount = 0;
            int ccBytes = 0, zeroBytes = 0, mixedBytes = 0;
            foreach (var u in g)
            {
                bool allCC = u.sample.All(b => b == 0xCC) && peFile.Buffer.ReadByte(u.start) == 0xCC;
                bool allZero = u.sample.All(b => b == 0);
                // Check full content for small unknowns
                if (u.length <= 64)
                {
                    allCC = true; allZero = true;
                    for (int i = u.start; i < u.start + u.length; i++)
                    {
                        byte b = peFile.Buffer.ReadByte(i);
                        if (b != 0xCC) allCC = false;
                        if (b != 0) allZero = false;
                    }
                }
                if (allCC) { ccCount++; ccBytes += u.length; }
                else if (allZero) { zeroCount++; zeroBytes += u.length; }
                else { mixedCount++; mixedBytes += u.length; }
            }
            
            System.Console.WriteLine($"  {g.Key}: {count} unknowns, {bytes:N0} bytes (CC:{ccCount}/{ccBytes:N0}b  Zero:{zeroCount}/{zeroBytes:N0}b  Data:{mixedCount}/{mixedBytes:N0}b)");
            
            // Show top 5 largest non-CC unknowns
            var top = g.Where(u => {
                if (u.length <= 64) {
                    for (int i = u.start; i < u.start + u.length; i++)
                        if (peFile.Buffer.ReadByte(i) != 0xCC) return true;
                    return false;
                }
                return u.sample.Any(b => b != 0xCC);
            }).OrderByDescending(u => u.length).Take(5);
            foreach (var u in top)
            {
                string hex = string.Join(" ", u.sample.Take(16).Select(b => b.ToString("X2")));
                System.Console.WriteLine($"    @0x{u.start:X} len={u.length} : {hex}");
            }
        }
        System.Console.WriteLine($"  TOTAL: {totalCount} unknowns, {totalBytes:N0} bytes");
    }

    static void CollectUnknowns(Node node, string currentSection, List<(string section, int start, int length, byte[] sample)> result)
    {
        if (node is GuiLabs.FileFormat.PE.Section)
        {
            currentSection = node.Text;
        }
        
        if (node is Unknown)
        {
            var buf = node.Buffer;
            int len = System.Math.Min(32, node.Length);
            var sample = new byte[len];
            for (int i = 0; i < len; i++)
                sample[i] = buf.ReadByte(node.Start + i);
            result.Add((currentSection, node.Start, node.Length, sample));
            return;
        }
        
        if (node.HasChildren)
        {
            foreach (var child in node.Children)
            {
                CollectUnknowns(child, currentSection, result);
            }
        }
    }
}
