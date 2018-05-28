using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;

namespace MetadataTools
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            PdbInfo.LogAction = Log;

            if (args.Length == 1)
            {
                if (args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var dll = Path.GetFullPath(args[0]);
                    if (!File.Exists(dll))
                    {
                        Error("dll file not found: " + dll);
                        return 1;
                    }

                    PrintAssemblyInfo(dll);
                }
                else if (args[0].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    DownloadSymbolsForAllFiles(Environment.CurrentDirectory, args[0]);
                    return 0;
                }
                else
                {
                    Error("Expected a .dll or .exe file as first argument");
                    return 2;
                }
            }
            else if (args.Length == 2)
            {
                if (args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var dll = Path.GetFullPath(args[0]);
                    if (!File.Exists(dll))
                    {
                        Error("dll file not found: " + dll);
                        return 3;
                    }

                    if (args[1].EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    {
                        var pdb = Path.GetFullPath(args[1]);
                        if (!File.Exists(pdb))
                        {
                            Error("pdb file not found: " + pdb);
                            return 4;
                        }

                        CheckMatch(dll, pdb);
                        return 0;
                    }
                    else if (args[1].StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        DownloadSymbols(dll, args[1]);
                        return 0;
                    }

                    var directory = Path.GetFullPath(args[1]);
                    if (Directory.Exists(directory))
                    {
                        FindMatchingPdb(dll, directory);
                    }
                    else
                    {
                        Error("Second argument must be an existing directory with .pdb files");
                        return 6;
                    }
                }
                else
                {
                    Error("Expected a .dll or .exe file as first argument");
                    return 5;
                }
            }

            return 0;
        }

        private static void DownloadSymbolsForAllFiles(string currentDirectory, string symbolPath)
        {
            IEnumerable<string> dlls = Directory.GetFiles(currentDirectory, "*.dll", SearchOption.AllDirectories);
            dlls = dlls.Concat(Directory.GetFiles(currentDirectory, "*.exe", SearchOption.AllDirectories));

            foreach (var file in dlls)
            {
                if (!PEFile.PEFileReader.IsManagedAssembly(file))
                {
                    continue;
                }

                var pdb = Path.ChangeExtension(file, ".pdb");
                if (!File.Exists(pdb))
                {
                    DownloadSymbols(file, symbolPath);
                }
                else if (!PdbInfo.IsMatch(file, pdb))
                {
                    File.Delete(pdb);
                    DownloadSymbols(file, pdb);
                }
            }
        }

        private static void DownloadSymbols(string dll, string url)
        {
            var pdbInfo = PdbInfo.Read(dll);
            foreach (var record in pdbInfo)
            {
                if (record.DownloadPdb(url))
                {
                    return;
                }
            }

            Log($"Couldn't find symbols for {dll} at {url}");
        }

        private static void PrintAssemblyInfo(string dll)
        {
            var debugDirectory = PdbInfo.ReadDebugDirectoryEntries(dll);
            foreach (var pdb in debugDirectory)
            {
                PrintNameValue("Debug directory entry", pdb.entry.Type.ToString());
                if (pdb.entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    CodeViewDebugDirectoryData data = (CodeViewDebugDirectoryData)pdb.data;
                    PrintNameValue("Guid", data.Guid.ToString());
                    PrintNameValue("Age", data.Age.ToString());
                    PrintNameValue("Pdb path", data.Path.ToString());
                    PrintNameValue("Stamp", pdb.entry.Stamp.ToString("X8"));
                }

                Console.WriteLine();
            }
        }

        private static void PrintNameValue(string name, string value)
        {
            Log((name + ": ").PadRight(10, ' '), ConsoleColor.DarkGray, lineBreak: false);
            Log(value, ConsoleColor.Gray);
        }

        private static void FindMatchingPdb(string dll, string directory)
        {
            var pdbs = Directory.GetFiles(
                directory,
                Path.GetFileNameWithoutExtension(dll) + ".pdb",
                SearchOption.AllDirectories);
            var debugDirectory = PdbInfo.Read(dll);
            foreach (var pdb in pdbs)
            {
                if (PdbInfo.IsMatch(debugDirectory, pdb))
                {
                    Log("Match: " + pdb, ConsoleColor.Green);
                }
            }
        }

        private static void CheckMatch(string dll, string pdb)
        {
            if (PdbInfo.IsMatch(dll, pdb))
            {
                Log("Match", ConsoleColor.Green);
            }
            else
            {
                Log("No match", ConsoleColor.Red);
            }
        }

        private static void Error(string text)
        {
            Console.Error.WriteLine(text);
        }

        private static void PrintHelp()
        {
            Log(@"Usage: Pdb.exe <input.dll> <input.pdb>
    Check if the .dll matches the .pdb.");
        }

        private static void Log(string text)
        {
            Log(text, ConsoleColor.DarkGray);
        }

        private static void Log(string text, ConsoleColor color, bool lineBreak = true)
        {
            lock (typeof(Program))
            {
                var oldColor = Console.ForegroundColor;
                if (oldColor != color)
                {
                    Console.ForegroundColor = color;
                }

                if (lineBreak)
                {
                    Console.WriteLine(text);
                }
                else
                {
                    Console.Write(text);
                }

                if (oldColor != color)
                {
                    Console.ForegroundColor = oldColor;
                }
            }
        }
    }
}
