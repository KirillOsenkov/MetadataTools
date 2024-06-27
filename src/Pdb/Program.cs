using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

            PdbRecord.LogAction = Log;

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
                else if (args[0].EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    var files = GetFiles(args[0]);
                    foreach (var file in files)
                    {
                        PrintPdbInfo(file);
                    }

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

                    bool found = PrintAssemblyInfo(dll);

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
                        if (!found)
                        {
                            DownloadSymbols(dll, args[1]);
                        }

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

        private static char[] pathSeparators = new [] { '\\', '/' };

        public static IEnumerable<string> GetFiles(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Enumerable.Empty<string>();
            }

            int lastSeparator = input.LastIndexOfAny(pathSeparators);
            if (lastSeparator == -1)
            {
                var files = Directory.GetFiles(Environment.CurrentDirectory, input, SearchOption.AllDirectories);
                return files;
            }
            else
            {
                if (lastSeparator > 0 && lastSeparator < input.Length - 1)
                {
                    var directory = input.Substring(0, lastSeparator);
                    directory = Path.GetFullPath(directory);
                    if (!Directory.Exists(directory))
                    {
                        return Enumerable.Empty<string>();
                    }

                    var pattern = input.Substring(lastSeparator + 1, input.Length - lastSeparator - 1);
                    var files = Directory.GetFiles(directory, pattern, SearchOption.AllDirectories);
                    return files;
                }
            }

            return Enumerable.Empty<string>();
        }

        private static void DownloadSymbolsForAllFiles(string currentDirectory, string symbolPath)
        {
            IEnumerable<string> dlls = Directory.GetFiles(currentDirectory, "*.dll", SearchOption.AllDirectories);
            dlls = dlls.Concat(Directory.GetFiles(currentDirectory, "*.exe", SearchOption.AllDirectories));

            foreach (var file in dlls)
            {
                if (!GuiLabs.Metadata.PEFile.IsManagedAssembly(file))
                {
                    continue;
                }

                var info = ModuleInfo.Read(file);
                if (info.HasEmbeddedPdb)
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

        private static bool DownloadSymbols(string dll, string url)
        {
            var moduleInfo = ModuleInfo.Read(dll);
            foreach (var pdbRecord in moduleInfo.PdbEntries)
            {
                if (pdbRecord.DownloadPdb(url) is string pdb)
                {
                    if (CheckMatch(dll, pdb))
                    {
                        PrintPdbInfo(pdb);
                        var sourceLink = ModuleInfo.ReadSourceLink(dll, false);
                        PrintSourceLink(sourceLink);
                        return true;
                    }
                }
            }

            Log($"Couldn't find symbols for {dll} at {url}");
            return false;
        }

        private static bool PrintAssemblyInfo(string dll)
        {
            var moduleInfo = ModuleInfo.Read(dll);
            Console.WriteLine(dll);
            Console.WriteLine();

            if (moduleInfo.Reproducible)
            {
                Console.WriteLine("Reproducible");
            }

            foreach (var pdbEntry in moduleInfo.PdbEntries)
            {
                var data = pdbEntry;
                PrintNameValue("Guid", data.Guid.ToString());
                PrintNameValue("Age", data.Age.ToString());
                PrintNameValue("Pdb path", data.Path.ToString());
                PrintNameValue("Stamp", pdbEntry.Stamp.ToString("X8"));
                Console.WriteLine();
            }

            bool found = false;

            if (moduleInfo.HasEmbeddedPdb)
            {
                Log("Contains embedded pdb", ConsoleColor.Green);
                found = true;
            }

            var pdb = Path.ChangeExtension(dll, ".pdb");
            if (!found && File.Exists(pdb))
            {
                Console.Write("Found " + Path.GetFileName(pdb) + ": ");
                found = CheckMatch(dll, pdb);
                PrintPdbInfo(pdb);
            }

            PrintSourceLink(moduleInfo.SourceLink);

            return found;
        }

        private static void PrintSourceLink(string sourceLink)
        {
            if (sourceLink != null)
            {
                Console.WriteLine();
                Log("SourceLink:", ConsoleColor.Green);
                Log(sourceLink, ConsoleColor.Gray);
            }
        }

        private static void PrintPdbInfo(string pdb)
        {
            Console.Write(pdb + ": ");

            using var streamReader = new StreamReader(pdb);

            char[] chars = new char[24];
            var count = streamReader.Read(chars, 0, chars.Length);
            if (count > 4 && new string(chars, 0, 4) == "BSJB")
            {
                Log("Portable pdb", ConsoleColor.Green);
                return;
            }

            if (count == 24)
            {
                if (new string(chars) == "Microsoft C/C++ MSF 7.00")
                {
                    Log("Native pdb: Microsoft C/C++ MSF 7.00", ConsoleColor.Blue);
                    return;
                }
            }

            Log("Not a portable pdb", ConsoleColor.Yellow);
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

            var moduleInfo = ModuleInfo.Read(dll);
            foreach (var pdb in pdbs)
            {
                if (PdbInfo.IsMatch(moduleInfo, pdb))
                {
                    Log("Match: " + pdb, ConsoleColor.Green);
                }
            }
        }

        private static bool CheckMatch(string dll, string pdb)
        {
            if (PdbInfo.IsMatch(dll, pdb))
            {
                Log("Match", ConsoleColor.Green);
                return true;
            }
            else
            {
                Log("No match", ConsoleColor.Red);
            }

            return false;
        }

        private static void Error(string text)
        {
            Console.Error.WriteLine(text);
        }

        private static void PrintHelp()
        {
            Log(@"Usages:
    Pdb.exe <input.dll>                  - Print assembly info and debug directory.
    Pdb.exe <http://symweb>              - Download symbols for all .dll/.exe in current directory.
    Pdb.exe <input.dll> <input.pdb>      - Check if the .dll matches the .pdb.
    Pdb.exe <input.dll> <http://symweb>  - Try to download .pdb for input.dll from the URL.
    Pdb.exe <input.dll> <Directory>      - Find matching .pdb for input.dll in Directory.
    Pdb.exe <*.pdb>                      - For each .pdb in a directory prints whether it's portable.
    Pdb.exe <input.pdb>                  - Prints whether input.pdb is portable.");
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
