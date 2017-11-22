using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Mono.Cecil;

namespace RefDump
{
    class Dumper
    {
        private string[] args;

        public string FilePath { get; private set; }
        public string OutputXml { get; set; }

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

            Log(assemblyDefinition.Name.FullName, ConsoleColor.Green);
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

            if (OutputXml != null)
            {
                var refTree = GetRefTree(assemblyDefinition);
                DumpToXml(refTree, OutputXml);
            }
        }

        private void DumpToXml(RefTree refTree, string outputXml)
        {
            var document = new XDocument();
            document.Add(new XElement("Assembly"));
            foreach (var asm in refTree.Assemblies.OrderBy(a => a.Key))
            {
                Dump(document.Root, asm);
            }

            document.Save(OutputXml);
        }

        private void Dump(XElement root, KeyValuePair<string, RefAssembly> asm)
        {
            var referenceElement = new XElement("Reference");
            referenceElement.SetAttributeValue("Name", asm.Key);
            foreach (var type in asm.Value.Types.OrderBy(t => t.Key))
            {
                Dump(referenceElement, type);
            }

            root.Add(referenceElement);
        }

        private void Dump(XElement referenceElement, KeyValuePair<string, RefType> type)
        {
            var typeElement = new XElement("Type");
            typeElement.SetAttributeValue("Name", type.Key);
            foreach (var item in type.Value.Members.OrderBy(m => m.FullName))
            {
                Dump(typeElement, item);
            }

            referenceElement.Add(typeElement);
        }

        private void Dump(XElement typeElement, MemberReference memberReference)
        {
            var memberElement = new XElement("Member");
            memberElement.SetAttributeValue("Name", memberReference.Name);
            memberElement.SetAttributeValue("FullName", memberReference.FullName);
            typeElement.Add(memberElement);
        }

        class RefTree
        {
            public Dictionary<string, RefAssembly> Assemblies { get; set; } = new Dictionary<string, RefAssembly>();

            public void AddMember(MemberReference memberReference)
            {
                var typeReference = memberReference.DeclaringType;
                var assembly = typeReference.Scope.ToString() ?? "<null>";

                if (!Assemblies.TryGetValue(assembly, out var refAssembly))
                {
                    refAssembly = new RefAssembly();
                    Assemblies[assembly] = refAssembly;
                }

                refAssembly.AddMember(memberReference);
            }
        }

        class RefAssembly
        {
            public Dictionary<string, RefType> Types { get; set; } = new Dictionary<string, RefType>();

            public void AddMember(MemberReference memberReference)
            {
                var typeReference = memberReference.DeclaringType;

                if (!Types.TryGetValue(typeReference.FullName, out var typeRef))
                {
                    typeRef = new RefType();
                    Types[typeReference.FullName] = typeRef;
                }

                typeRef.AddMember(memberReference);
            }
        }

        class RefType
        {
            public List<MemberReference> Members { get; set; } = new List<MemberReference>();

            public void AddMember(MemberReference memberReference)
            {
                Members.Add(memberReference);
            }
        }

        private RefTree GetRefTree(AssemblyDefinition assemblyDefinition)
        {
            var refTree = new RefTree();

            foreach (var memberReference in assemblyDefinition.MainModule.GetMemberReferences())
            {
                if (memberReference.DeclaringType.IsArray)
                {
                    continue;
                }

                if (memberReference.DeclaringType.Scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference)
                {
                    continue;
                }

                refTree.AddMember(memberReference);
            }

            return refTree;
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

        private static void Log(string text = "", ConsoleColor color = ConsoleColor.Gray, bool lineBreak = true)
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
                    continue;
                }

                if (arg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    OutputXml = Path.GetFullPath(arg);
                    continue;
                }
            }
        }

        private static void PrintUsage()
        {
            Log(@"Usage: ", ConsoleColor.Green, lineBreak: false);
            Log(@"refdump file.dll [output.xml]", ConsoleColor.White);

    Log(@"    Lists all references of the input assembly, 
    all types and all members consumed from each reference.
    If an output.xml file name is specified, dump detailed 
    report into that xml.", ConsoleColor.Gray);
        }
    }
}
