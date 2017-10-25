using System;
using System.IO;

namespace MetadataTools
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintHelp();
                return;
            }

            PdbInfo.LogAction = Log;

            if (args.Length == 2 && 
                args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                args[1].EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                var dll = Path.GetFullPath(args[0]);
                var pdb = Path.GetFullPath(args[1]);

                if (!File.Exists(dll))
                {
                    Error("dll file not found: " + dll);
                    return;
                }

                if (!File.Exists(pdb))
                {
                    Error("pdb file not found: " + pdb);
                    return;
                }

                CheckMatch(dll, pdb);
            }
        }

        private static void CheckMatch(string dll, string pdb)
        {
            if (PdbInfo.IsMatch(dll, pdb))
            {
                Log("Match");
            }
            else
            {
                Log("No match");
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
            Console.WriteLine(text);
        }
    }
}
