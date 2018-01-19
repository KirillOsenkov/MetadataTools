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
        public string FilePath { get; private set; }
        public string OutputXml { get; set; }
        public bool OutputTypes { get; set; } = false;
        public bool OutputMembers { get; set; } = false;

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            var dumper = new Dumper();
            if (!dumper.ParseArgs(args))
            {
                PrintUsage();
                return;
            }

            dumper.DoWork();
        }

        private void DoWork()
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

            var refTree = GetRefTree(assemblyDefinition);

            if (OutputTypes || OutputMembers)
            {
                DumpToConsole(refTree);
            }

            if (OutputXml != null)
            {
                DumpToXml(refTree, OutputXml);
            }
        }

        private void DumpToConsole(RefTree refTree)
        {
            Log();

            foreach (var kvp in refTree.Assemblies.OrderBy(a => a.Key))
            {
                Log(kvp.Key + ":", ConsoleColor.Cyan);

                if (OutputTypes || OutputMembers)
                {
                    foreach (var typeRef in kvp.Value.Types.OrderBy(t => t.Key))
                    {
                        var text = "    " + typeRef.Key;

                        if (OutputMembers)
                        {
                            PrintWithHighlight(text, text.LastIndexOf('.'), ConsoleColor.DarkGreen, ConsoleColor.Green);
                            foreach (var memberRef in typeRef.Value.Members.OrderBy(m => m.FullName))
                            {
                                text = "        " + memberRef.FullName;
                                PrintWithHighlight(text, text.LastIndexOf('.'), ConsoleColor.Gray, ConsoleColor.White);
                            }
                        }
                        else
                        {
                            PrintWithHighlight(text, text.LastIndexOf('.'), ConsoleColor.DarkGray, ConsoleColor.Gray);
                        }
                    }

                    Log();
                }
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

            if (OutputTypes)
            {
                foreach (var type in asm.Value.Types.OrderBy(t => t.Key))
                {
                    Dump(referenceElement, type);
                }
            }

            root.Add(referenceElement);
        }

        private void Dump(XElement referenceElement, KeyValuePair<string, RefType> type)
        {
            var typeElement = new XElement("Type");
            typeElement.SetAttributeValue("Name", type.Key);

            if (OutputMembers)
            {
                foreach (var item in type.Value.Members.OrderBy(m => m.FullName))
                {
                    Dump(typeElement, item);
                }
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

            public void AddType(TypeReference typeReference)
            {
                RefAssembly refAssembly = GetAssembly(typeReference);
                if (refAssembly != null)
                {
                    refAssembly.AddType(typeReference);
                }
            }

            public void AddMember(MemberReference memberReference)
            {
                var typeReference = memberReference.DeclaringType;
                RefAssembly refAssembly = GetAssembly(typeReference);
                if (refAssembly != null)
                {
                    refAssembly.AddMember(memberReference);
                }
            }

            public RefAssembly GetAssembly(TypeReference typeReference)
            {
                if (!IsValidType(typeReference))
                {
                    return null;
                }

                var assemblyName = typeReference.Scope.ToString() ?? "<null>";

                if (!Assemblies.TryGetValue(assemblyName, out var refAssembly))
                {
                    refAssembly = new RefAssembly();
                    Assemblies[assemblyName] = refAssembly;
                }

                return refAssembly;
            }
        }

        class RefAssembly
        {
            public Dictionary<string, RefType> Types { get; set; } = new Dictionary<string, RefType>();

            public void AddMember(MemberReference memberReference)
            {
                RefType typeRef = AddType(memberReference.DeclaringType);
                if (typeRef != null)
                {
                    typeRef.AddMember(memberReference);
                }
            }

            public RefType AddType(TypeReference typeReference)
            {
                if (!Types.TryGetValue(typeReference.FullName, out var refType))
                {
                    if (!IsValidType(typeReference))
                    {
                        return null;
                    }

                    refType = new RefType();
                    Types[typeReference.FullName] = refType;
                }

                return refType;
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

            foreach (var typeReference in assemblyDefinition.MainModule.GetTypeReferences())
            {
                refTree.AddType(typeReference);
            }

            foreach (var memberReference in assemblyDefinition.MainModule.GetMemberReferences())
            {
                if (memberReference.DeclaringType.Scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference)
                {
                    continue;
                }

                refTree.AddMember(memberReference);
            }

            return refTree;
        }

        public static bool IsValidType(TypeReference typeReference)
        {
            return typeReference != null &&
                !typeReference.IsArray &&
                typeReference.Scope.MetadataScopeType == MetadataScopeType.AssemblyNameReference;
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

        private bool ParseArgs(string[] args)
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

                if (arg == "-t" || arg == "/t")
                {
                    OutputTypes = true;
                    continue;
                }

                if (arg == "-m" || arg == "/m")
                {
                    OutputMembers = true;
                    continue;
                }

                Log("Unknown argument: " + arg, ConsoleColor.Red);
                return false;
            }

            return true;
        }

        private static void PrintUsage()
        {
            Log(@"Usage: ", ConsoleColor.Green, lineBreak: false);
            Log(@"refdump file.dll [-t] [-m] [output.xml]", ConsoleColor.White);

            Log(@"    Lists all references of the input assembly.
    -t    List all used types
    -m    List all used members

    If an output.xml file name is specified, dump detailed 
    report into that xml.", ConsoleColor.Gray);
        }
    }
}
