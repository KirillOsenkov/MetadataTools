using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FindUnusedMembers
{
    class Program
    {
        private readonly List<string> targetFiles = new List<string>();
        private readonly List<string> consumerSpecs = new List<string>();
        private readonly List<string> resolveDirectories = new List<string>();
        private readonly List<string> rootFiles = new List<string>();
        private readonly List<string> whyQueries = new List<string>();
        private string outputFile;
        private bool keepPublicApi;
        private bool verbose;
        private string dumpBaml;

        static int Main(string[] args)
        {
            var program = new Program();
            if (!program.ParseArgs(args))
            {
                PrintUsage();
                return -1;
            }

            return program.Run();
        }

        private bool ParseArgs(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            foreach (var arg in args)
            {
                if (arg is "-?" or "/?" or "-h" or "/h" or "-help" or "--help")
                {
                    return false;
                }

                if (TryGetValue(arg, "c", out var value))
                {
                    consumerSpecs.Add(value);
                }
                else if (TryGetValue(arg, "r", out value))
                {
                    resolveDirectories.Add(value);
                }
                else if (TryGetValue(arg, "roots", out value))
                {
                    rootFiles.Add(value);
                }
                else if (TryGetValue(arg, "why", out value))
                {
                    whyQueries.Add(value);
                }
                else if (TryGetValue(arg, "out", out value))
                {
                    outputFile = value;
                }
                else if (arg is "-keepPublic" or "/keepPublic")
                {
                    keepPublicApi = true;
                }
                else if (arg is "-v" or "/v")
                {
                    verbose = true;
                }
                else if (TryGetValue(arg, "dumpBaml", out value))
                {
                    dumpBaml = value;
                }
                else if (!arg.StartsWith("-") && File.Exists(arg))
                {
                    targetFiles.Add(Path.GetFullPath(arg));
                }
                else
                {
                    Console.Error.WriteLine("Unknown argument or file not found: " + arg);
                    return false;
                }
            }

            if (targetFiles.Count == 0)
            {
                Console.Error.WriteLine("Specify at least one target assembly.");
                return false;
            }

            return true;
        }

        private static bool TryGetValue(string arg, string name, out string value)
        {
            value = null;
            foreach (var prefix in new[] { "-", "/" })
            {
                var start = prefix + name + ":";
                if (arg.StartsWith(start, StringComparison.OrdinalIgnoreCase) && arg.Length > start.Length)
                {
                    value = arg.Substring(start.Length).Trim('"');
                    return true;
                }
            }

            return false;
        }

        private int Run()
        {
            if (dumpBaml != null)
            {
                foreach (var file in targetFiles)
                {
                    BamlScanner.Dump(file, dumpBaml, Console.Out);
                }

                return 0;
            }

            var resolver = new Resolver();
            foreach (var directory in resolveDirectories)
            {
                resolver.AddSearchDirectory(Path.GetFullPath(directory));
            }

            var analyzer = new Analyzer(resolver);
            foreach (var file in targetFiles)
            {
                analyzer.AddTarget(file);
            }

            analyzer.ComputeInteropTypes();

            bool keepPublic = keepPublicApi || consumerSpecs.Count == 0;
            analyzer.AddInherentRoots(keepPublic);

            foreach (var file in rootFiles)
            {
                analyzer.AddRootsFromFile(file);
            }

            foreach (var file in GetConsumerFiles())
            {
                analyzer.AddConsumer(file);
            }

            analyzer.Run();

            var report = new Report(analyzer, keepPublic, verbose);
            var text = report.Build(whyQueries);
            if (outputFile != null)
            {
                File.WriteAllText(outputFile, text);
                Console.WriteLine(report.Summary);
                Console.WriteLine("Report written to " + Path.GetFullPath(outputFile));
            }
            else
            {
                Console.Write(text);
            }

            return 0;
        }

        private IEnumerable<string> GetConsumerFiles()
        {
            var result = new List<string>();
            foreach (var spec in consumerSpecs)
            {
                var fullPath = Path.GetFullPath(spec);
                if (File.Exists(fullPath))
                {
                    result.Add(fullPath);
                    continue;
                }

                if (Directory.Exists(fullPath))
                {
                    result.AddRange(Directory.EnumerateFiles(fullPath, "*.dll", SearchOption.AllDirectories));
                    result.AddRange(Directory.EnumerateFiles(fullPath, "*.exe", SearchOption.AllDirectories));
                    continue;
                }

                var directory = Path.GetDirectoryName(fullPath);
                var pattern = Path.GetFileName(fullPath);
                if (Directory.Exists(directory))
                {
                    result.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories));
                    continue;
                }

                Console.Error.WriteLine("Consumer not found: " + spec);
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: FindUnusedMembers <target.dll>... [-c:<consumer>]... [options]

Lists the types and members of the target assemblies that cannot be reached
from the consumer assemblies. Reachability is computed from IL, metadata and
compiled XAML (BAML) in the targets and the consumers.

  <target.dll>      Assembly to analyze. Several targets are analyzed together.
  -c:<path>         Consumer assemblies: a file, a directory (searched
                    recursively for *.dll and *.exe), or dir\pattern.
                    Repeatable. Copies of the targets in it are ignored.
                    Without -c the public API of the targets is kept, and
                    the report shows unused internal code.
  -keepPublic       Treat the public and protected API of the targets as used.
  -roots:<file>     Extra roots, one documentation ID per line (T:, M:, F:,
                    P:, E:). A trailing * matches a prefix. # starts a comment.
  -r:<dir>          Extra directory to resolve references from. Repeatable.
  -why:<text>       For every used member whose ID contains <text>, print the
                    chain of references that keeps it. Repeatable.
  -out:<file>       Write the report to a file.
  -v                Also list resolution warnings.
  -dumpBaml:<name>  Print the records of the target BAML resources whose name
                    contains <name> (diagnostics).

The report lists documentation IDs with source locations (when a PDB is
available). Before deleting a member, verify that it is not used through
reflection, dynamic, serialization or string-based binding, and see the
'Reflection call sites' section of the report.

Examples:
  FindUnusedMembers Lib.dll -c:C:\App\bin\Debug -out:unused.txt
  FindUnusedMembers Lib.dll -c:C:\App\bin\Debug -why:Foo.Bar
  FindUnusedMembers Lib.dll");
        }
    }

    class Report
    {
        private readonly Analyzer analyzer;
        private readonly bool keepPublic;
        private readonly bool verbose;
        private readonly StringBuilder sb = new StringBuilder();

        public string Summary { get; private set; }

        public Report(Analyzer analyzer, bool keepPublic, bool verbose)
        {
            this.analyzer = analyzer;
            this.keepPublic = keepPublic;
            this.verbose = verbose;
        }

        public string Build(IReadOnlyList<string> whyQueries)
        {
            var unusedTypes = new List<(string Id, string Location)>();
            var unusedMembers = new List<(string Id, string Location)>();
            var unusedAccessors = new List<(string Id, string Location)>();
            var constants = new List<(string Id, string Location)>();
            int totalTypes = 0;
            int totalMembers = 0;

            foreach (var assembly in analyzer.Targets)
            {
                foreach (var type in Analyzer.GetAllTypes(assembly.MainModule))
                {
                    if (IsCompilerGenerated(type))
                    {
                        continue;
                    }

                    totalTypes++;
                    if (!analyzer.IsMarked(type))
                    {
                        if (type.IsNested && !analyzer.IsMarked(type.DeclaringType))
                        {
                            continue;
                        }

                        unusedTypes.Add((DocumentationId.Get(type), GetLocation(type)));
                        continue;
                    }

                    foreach (var member in Analyzer.GetMembers(type))
                    {
                        if (member.Name.Contains('<'))
                        {
                            continue;
                        }

                        totalMembers++;
                        CollectMember(member, unusedMembers, unusedAccessors, constants);
                    }
                }
            }

            Summary = $"{unusedTypes.Count} of {totalTypes} types and {unusedMembers.Count} of {totalMembers} members in used types are unused; {unusedAccessors.Count} unused accessors, {constants.Count} constants to check by hand.";

            sb.AppendLine("Targets:");
            foreach (var assembly in analyzer.Targets)
            {
                sb.AppendLine("    " + assembly.MainModule.FileName);
            }

            sb.AppendLine($"Consumers that reference the targets: {analyzer.Consumers.Count}");
            foreach (var consumer in analyzer.Consumers)
            {
                sb.AppendLine("    " + consumer);
            }

            sb.AppendLine($"Consumers that use the targets only in XAML: {analyzer.BamlConsumers.Count}");
            foreach (var consumer in analyzer.BamlConsumers)
            {
                sb.AppendLine("    " + consumer);
            }

            if (keepPublic)
            {
                sb.AppendLine("Public and protected API of the targets is treated as used.");
            }

            sb.AppendLine(Summary);
            sb.AppendLine();

            WriteSection("Unused types", unusedTypes);
            WriteSection("Unused members", unusedMembers);
            WriteSection("Unused accessors of used properties", unusedAccessors);
            WriteSection("Constants (compilers copy constant values into the code that uses them, so usage is not visible in IL; check the source)", constants);

            sb.AppendLine($"Reflection call sites ({analyzer.ReflectionSites.Count}) - members named by these strings were kept; check by hand:");
            foreach (var site in analyzer.ReflectionSites.OrderBy(s => s.Assembly).ThenBy(s => s.Method))
            {
                var strings = site.Strings.Count == 0 ? "" : "  strings: " + string.Join(", ", site.Strings.Distinct().Take(10).Select(s => "\"" + s + "\""));
                sb.AppendLine($"    {site.Assembly}: {site.Method} calls {site.Api}{strings}");
            }

            sb.AppendLine();

            if (verbose && analyzer.Warnings.Count > 0)
            {
                sb.AppendLine($"Warnings ({analyzer.Warnings.Count}):");
                foreach (var warning in analyzer.Warnings.OrderBy(w => w))
                {
                    sb.AppendLine("    " + warning);
                }

                sb.AppendLine();
            }

            foreach (var query in whyQueries)
            {
                WriteWhy(query);
            }

            return sb.ToString();
        }

        private void CollectMember(
            IMemberDefinition member,
            List<(string, string)> unusedMembers,
            List<(string, string)> unusedAccessors,
            List<(string, string)> constants)
        {
            switch (member)
            {
                case MethodDefinition method:
                    if (analyzer.IsAccessor(method) || (method.IsConstructor && method.IsStatic) || analyzer.IsMarked(method))
                    {
                        return;
                    }

                    if (IsTrivialDefaultConstructor(method))
                    {
                        return;
                    }

                    unusedMembers.Add((DocumentationId.Get(method), GetLocation(method)));
                    return;
                case FieldDefinition field:
                    if (field.IsLiteral && field.HasConstant && !field.DeclaringType.IsEnum)
                    {
                        if (!analyzer.IsMarked(field))
                        {
                            constants.Add((DocumentationId.Get(field), GetLocation(field.DeclaringType)));
                        }

                        return;
                    }

                    if (analyzer.IsMarked(field))
                    {
                        return;
                    }

                    var suffix = analyzer.IsWritten(field) ? "  (written but never read)" : "";
                    unusedMembers.Add((DocumentationId.Get(field) + suffix, GetLocation(field.DeclaringType)));
                    return;
                case PropertyDefinition property:
                    if (!analyzer.IsMarked(property))
                    {
                        var backingField = property.DeclaringType.Fields.FirstOrDefault(f => f.Name == "<" + property.Name + ">k__BackingField");
                        var written = backingField != null && analyzer.IsWritten(backingField) ? "  (written but never read)" : "";
                        unusedMembers.Add((DocumentationId.Get(property) + written, GetLocation(property)));
                        return;
                    }

                    if (IsWrittenOnlyInsideTarget(property))
                    {
                        unusedMembers.Add((DocumentationId.Get(property) + "  (written but never read)", GetLocation(property)));
                        return;
                    }

                    if (property.GetMethod != null && !analyzer.IsMarked(property.GetMethod))
                    {
                        unusedAccessors.Add((DocumentationId.Get(property) + " getter", GetLocation(property.GetMethod)));
                    }

                    if (property.SetMethod != null && !analyzer.IsMarked(property.SetMethod))
                    {
                        unusedAccessors.Add((DocumentationId.Get(property) + " setter", GetLocation(property.SetMethod)));
                    }

                    return;
                case EventDefinition @event:
                    if (!analyzer.IsMarked(@event))
                    {
                        unusedMembers.Add((DocumentationId.Get(@event), GetLocation(@event.AddMethod)));
                    }

                    return;
            }
        }

        private bool IsWrittenOnlyInsideTarget(PropertyDefinition property)
        {
            if (property.GetMethod == null || property.SetMethod == null || property.HasParameters)
            {
                return false;
            }

            if (analyzer.IsMarked(property.GetMethod) || !analyzer.IsMarked(property.SetMethod))
            {
                return false;
            }

            if (property.GetMethod.IsVirtual || property.SetMethod.IsVirtual)
            {
                return false;
            }

            bool isAutoProperty = property.DeclaringType.Fields.Any(f => f.Name == "<" + property.Name + ">k__BackingField");
            if (!isAutoProperty)
            {
                return false;
            }

            return !analyzer.IsReferencedByConsumer(property.SetMethod) && !analyzer.IsReferencedByConsumer(property);
        }

        private static bool IsCompilerGenerated(TypeDefinition type)
        {
            for (var current = type; current != null; current = current.DeclaringType)
            {
                if (current.Name.Contains('<'))
                {
                    return true;
                }

                if (current.HasCustomAttributes && current.CustomAttributes.Any(a =>
                    a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute" ||
                    a.AttributeType.FullName == "Microsoft.CodeAnalysis.EmbeddedAttribute"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsTrivialDefaultConstructor(MethodDefinition method)
        {
            if (!method.IsConstructor || method.IsStatic || method.Parameters.Count != 0 || !method.HasBody)
            {
                return false;
            }

            var instructions = method.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToArray();
            return instructions.Length == 3 &&
                instructions[0].OpCode.Code == Code.Ldarg_0 &&
                instructions[1].OpCode.Code == Code.Call &&
                instructions[1].Operand is MethodReference baseConstructor && baseConstructor.Name == ".ctor" &&
                baseConstructor.Parameters.Count == 0 &&
                instructions[2].OpCode.Code == Code.Ret;
        }

        private void WriteSection(string title, List<(string Id, string Location)> items)
        {
            sb.AppendLine($"{title} ({items.Count}):");
            foreach (var item in items.OrderBy(i => i.Id, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(item.Location))
                {
                    sb.AppendLine("    " + item.Id);
                }
                else
                {
                    sb.AppendLine("    " + item.Id + "    " + item.Location);
                }
            }

            sb.AppendLine();
        }

        private void WriteWhy(string query)
        {
            var matches = analyzer.GetAllTargetMembers()
                .Where(m => DocumentationId.Get(m).Contains(query, StringComparison.Ordinal))
                .ToArray();

            sb.AppendLine($"Why '{query}' ({matches.Length} matches):");
            foreach (var member in matches)
            {
                if (!analyzer.IsMarked(member))
                {
                    sb.AppendLine("    " + DocumentationId.Get(member) + ": unused");
                    continue;
                }

                sb.AppendLine("    " + DocumentationId.Get(member));
                var visited = new HashSet<IMemberDefinition>();
                var current = member;
                string indent = "        ";
                while (current != null && visited.Add(current) && visited.Count < 40)
                {
                    var reason = analyzer.GetReason(current);
                    if (reason == null)
                    {
                        break;
                    }

                    if (reason.From == null)
                    {
                        sb.AppendLine(indent + "<- " + reason.Text);
                        break;
                    }

                    sb.AppendLine(indent + "<- " + reason.Text + ": " + DocumentationId.Get(reason.From));
                    current = reason.From;
                }
            }

            sb.AppendLine();
        }

        private static string GetLocation(IMemberDefinition member)
        {
            switch (member)
            {
                case MethodDefinition method:
                    var point = GetFirstSequencePoint(method);
                    return point == null ? null : $"{point.Document.Url}({point.StartLine})";
                case PropertyDefinition property:
                    return GetLocation(property.GetMethod) ?? GetLocation(property.SetMethod);
                case TypeDefinition type:
                    foreach (var method in type.Methods)
                    {
                        var typePoint = GetFirstSequencePoint(method);
                        if (typePoint != null)
                        {
                            return typePoint.Document.Url;
                        }
                    }

                    foreach (var nested in type.NestedTypes)
                    {
                        var nestedLocation = GetLocation(nested);
                        if (nestedLocation != null)
                        {
                            return nestedLocation;
                        }
                    }

                    return null;
                default:
                    return null;
            }
        }

        private static SequencePoint GetFirstSequencePoint(MethodDefinition method)
        {
            if (method == null || !method.HasBody)
            {
                return null;
            }

            try
            {
                return method.DebugInformation?.SequencePoints?.FirstOrDefault(p => !p.IsHidden);
            }
            catch
            {
                return null;
            }
        }
    }
}
