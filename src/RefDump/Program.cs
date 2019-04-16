using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
using Mono.Cecil;

namespace RefDump
{
    class Dumper
    {
        public string FileSpec { get; private set; }
        public string OutputXml { get; set; }
        public string FilterToAssembly { get; set; }
        public bool OutputTypes { get; set; } = false;
        public bool OutputMembers { get; set; } = false;
        public bool Recursive { get; set; } = false;
        public bool GenerateGraph { get; set; } = false;

        [STAThread ]
        static void Main(string[] args)
        {
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
            XDocument document = null;
            XElement rootXml = null;

            if (OutputXml != null)
            {
                document = new XDocument();
                rootXml = new XElement("Assemblies");
            }

            if (FilterToAssembly != null)
            {
                Log($"References containing \"{FilterToAssembly}\":", ConsoleColor.Green);
            }

            if (FileSpec.Contains("*") || FileSpec.Contains("?"))
            {
                var root = Environment.CurrentDirectory;
                var separator = FileSpec.LastIndexOf('\\');
                if (separator > -1)
                {
                    root = FileSpec.Substring(0, separator);
                    root = Path.GetFullPath(root);

                    if (separator == FileSpec.Length - 1)
                    {
                        FileSpec = "*.dll";
                    }
                    else
                    {
                        FileSpec = FileSpec.Substring(separator + 1);
                    }
                }

                var files = Directory.GetFiles(root, FileSpec, Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                foreach (var file in files)
                {
                    DumpAssembly(file, rootXml);
                }
            }
            else if (File.Exists(FileSpec))
            {
                DumpAssembly(FileSpec, rootXml);
            }
            else
            {
                Console.Error.WriteLine("File(s) not found: " + FileSpec);
            }

            if (GenerateGraph)
            {
                GenerateGraphFile();
            }

            if (OutputXml != null)
            {
                document.Save(OutputXml);
            }
        }

        private void GenerateGraphFile()
        {
            var sb = new StringBuilder();

            sb.AppendLine("digraph G {");

            var assembliesWeCareAbout = new HashSet<string>(referenceGraph.Keys.Select(k => k.Name.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in referenceGraph)
            {
                foreach (var reference in kvp.Value)
                {
                    if (!assembliesWeCareAbout.Contains(reference.Name))
                    {
                        continue;
                    }

                    var line = $"  \"{kvp.Key.Name.Name}\" -> \"{reference.Name}\"";
                    sb.AppendLine(line);
                }
            }

            sb.AppendLine("}");

            Clipboard.SetText(sb.ToString());
        }

        private void DumpAssembly(string filePath, XElement rootXml = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                Console.WriteLine("Need to specify an input assembly.");
                return;
            }

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File {filePath} does not exist");
                return;
            }

            if (!PEFile.PEFileReader.IsManagedAssembly(filePath))
            {
                // Console.WriteLine($"{filePath} is not a managed assembly.");
                return;
            }

            var readerParameters = new ReaderParameters
            {
                InMemory = true
            };

            try
            {
                using (var assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath, readerParameters))
                {
                    var references = assemblyDefinition.MainModule.AssemblyReferences.OrderBy(r => r.FullName).ToArray();
                    if (FilterToAssembly != null)
                    {
                        references = references.Where(r => r.FullName.IndexOf(FilterToAssembly, StringComparison.OrdinalIgnoreCase) != -1).ToArray();
                    }

                    if (references.Length == 0)
                    {
                        // don't output anything if none of the references match the desired one
                        return;
                    }

                    ReportReferences(assemblyDefinition, references);

                    Log(assemblyDefinition.Name.FullName, ConsoleColor.Green);

                    foreach (var reference in references)
                    {
                        PrintWithHighlight(reference.FullName, reference.FullName.IndexOf(','), ConsoleColor.White, ConsoleColor.Gray);
                    }

                    var refTree = GetRefTree(assemblyDefinition);

                    if (OutputTypes || OutputMembers)
                    {
                        DumpToConsole(refTree);
                    }

                    Log();

                    if (rootXml != null)
                    {
                        DumpToXml(filePath, refTree, rootXml);
                    }
                }
            }
            catch
            {
            }
        }

        private Dictionary<AssemblyDefinition, AssemblyNameReference[]> referenceGraph = new Dictionary<AssemblyDefinition, AssemblyNameReference[]>();

        private void ReportReferences(AssemblyDefinition assemblyDefinition, AssemblyNameReference[] references)
        {
            referenceGraph[assemblyDefinition] = references;
        }

        private void DumpToConsole(RefTree refTree)
        {
            Log();

            foreach (var kvp in refTree.Assemblies.OrderBy(a => a.Key))
            {
                if (FilterToAssembly != null && kvp.Key.IndexOf(FilterToAssembly, StringComparison.OrdinalIgnoreCase) == -1)
                {
                    continue;
                }

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

        private void DumpToXml(string filePath, RefTree refTree, XElement rootXml)
        {
            var root = new XElement("Assembly", new XAttribute("File", filePath));
            rootXml.Add(root);

            foreach (var asm in refTree.Assemblies.OrderBy(a => a.Key))
            {
                Dump(root, asm);
            }
        }

        private void Dump(XElement root, KeyValuePair<string, RefAssembly> asm)
        {
            if (FilterToAssembly != null && asm.Key.IndexOf(FilterToAssembly, StringComparison.OrdinalIgnoreCase) == -1)
            {
                return;
            }

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

            public RefType AddType(TypeReference typeReference)
            {
                RefAssembly refAssembly = GetAssembly(typeReference);
                if (refAssembly != null)
                {
                    return refAssembly.AddType(typeReference);
                }

                return null;
            }

            public void AddMember(MemberReference memberReference)
            {
                var typeReference = memberReference.DeclaringType;
                var refType = AddType(typeReference);
                if (refType != null)
                {
                    refType.AddMember(memberReference);
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

            public RefType AddType(TypeReference typeReference)
            {
                if (typeReference == null)
                {
                    return null;
                }

                if (typeReference is GenericInstanceType generic)
                {
                    typeReference = generic.ElementType;
                }

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
            public HashSet<MemberReference> Members { get; set; } = new HashSet<MemberReference>();

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
                var scope = memberReference.DeclaringType.Scope;
                if (scope == null)
                {
                    continue;
                }

                if (scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference)
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
            if (args.Length == 0)
            {
                return false;
            }

            foreach (var arg in args)
            {
                if (arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    arg.Contains("*") ||
                    arg.Contains("?"))
                {
                    FileSpec = arg;
                    continue;
                }

                if (arg.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !arg.StartsWith("-") && !arg.StartsWith("/"))
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

                if (arg == "-g" || arg == "/g")
                {
                    GenerateGraph = true;
                    continue;
                }

                if (string.Equals(arg, "-s") || string.Equals(arg, "/s"))
                {
                    Recursive = true;
                    continue;
                }

                if ((arg.StartsWith("-a:", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("/a:", StringComparison.OrdinalIgnoreCase)) &&
                    arg.Length > 3)
                {
                    FilterToAssembly = arg.Substring(3);
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
            Log(@"refdump file.dll [-a:<refname>] [-t] [-m] [-s] [output.xml]", ConsoleColor.White);

            Log(@"    Lists all references of the input assembly(ies).
    (could be a file mask such as *.dll)
    -t    List all used types
    -m    List all used members
    -a:   Narrow results to a particular reference assembly,
          <refname> is a substring of the reference assembly
          name.
    -s    If the file pattern is specified, such as *.dll, 
          -s or /s indicates that the pattern should recurse
          into all subdirectories to find *.dll files.

    If an output.xml file name is specified, dump detailed 
    report into that xml.", ConsoleColor.Gray);
        }
    }
}
