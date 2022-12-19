using Ref12.Diagnostics.Extraction;
using System;
using System.IO;

namespace DumpModules
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: DumpModules.exe dumpPath outputDirectory");
            }

            string dumpPath = args[0];
            string outputDirectory = args[1];
            ModuleDumper.Run(dumpPath, outputDirectory);
        }
    }
}
