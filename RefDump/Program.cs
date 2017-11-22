using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace RefDump
{
    class Dumper
    {
        private string[] args;

        public string FilePath { get; private set; }

        public Dumper(string[] args)
        {
            this.args = args;
            ParseArgs();
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            var dumper = new Dumper(args);
            dumper.Dump();
        }

        private void Dump()
        {
            if (string.IsNullOrEmpty(FilePath))
            {
                Console.WriteLine("Need to specify an input assembly.");
                return;
            }

            if (!File.Exists(FilePath))
            {
                Console.WriteLine($"File {FilePath} does not exist");
                return;
            }

            var readerParameters = new ReaderParameters
            {
                InMemory = true
            };
            var assemblyDefinition = AssemblyDefinition.ReadAssembly(FilePath, readerParameters);
            Log(assemblyDefinition.Name.FullName);
            Log();
            Log("References:", ConsoleColor.Green);
            foreach (var reference in assemblyDefinition.MainModule.AssemblyReferences.OrderBy(r => r.FullName))
            {
                PrintWithHighlight(reference.FullName, reference.FullName.IndexOf(','), ConsoleColor.White, ConsoleColor.Gray);
            }

            Log("");

            var typeReferencesByScope = new Dictionary<string, List<string>>();
            foreach (var typeReference in assemblyDefinition.MainModule.GetTypeReferences())
            {
                if (typeReference.IsArray)
                {
                    continue;
                }

                var scope = typeReference.Scope?.ToString() ?? "<null>";
                if (!typeReferencesByScope.TryGetValue(scope, out var bucket))
                {
                    bucket = new List<string>();
                    typeReferencesByScope[scope] = bucket;
                }

                bucket.Add(typeReference.FullName);
            }

            foreach (var kvp in typeReferencesByScope.OrderBy(kvp => kvp.Key))
            {
                Log(kvp.Key + ":", ConsoleColor.Cyan);
                foreach (var typeRef in kvp.Value.OrderBy(t => t))
                {
                    var text = "    " + typeRef;
                    PrintWithHighlight(text, text.LastIndexOf('.'), ConsoleColor.Gray, ConsoleColor.White);
                }
            }
        }

        private void PrintWithHighlight(string originalString, int splitPosition, ConsoleColor firstPart, ConsoleColor secondPart)
        {
            if (splitPosition != -1)
            {
                var firstPartText = originalString.Substring(0, splitPosition);
                var secondPartText = originalString.Substring(splitPosition + 1, originalString.Length - splitPosition - 1);
                Log(firstPartText + originalString[splitPosition], firstPart, lineBreak: false);
                Log(secondPartText, secondPart);
            }
            else
            {
                Log(originalString, firstPart);
            }
        }

        private void Log(string text = "", ConsoleColor color = ConsoleColor.Gray, bool lineBreak = true)
        {
            Console.ForegroundColor = color;
            if (lineBreak)
            {
                Console.WriteLine(text);
            }
            else
            {
                Console.Write(text);
            }

            Console.ResetColor();
        }

        private void ParseArgs()
        {
            foreach (var arg in args)
            {
                if ((arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) && File.Exists(arg))
                {
                    FilePath = Path.GetFullPath(arg);
                }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: refdump file.dll
    Lists all references of the input assembly, 
    all types and all members consumed from each reference.");
        }
    }
}
