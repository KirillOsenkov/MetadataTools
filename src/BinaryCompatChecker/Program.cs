using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public class Checker
    {
        Dictionary<AssemblyDefinition, HashSet<string>> assemblyToTypeList = new Dictionary<AssemblyDefinition, HashSet<string>>();
        StringBuilder sb = new StringBuilder();
        Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        HashSet<string> unresolvedAssemblies = new HashSet<string>();
        HashSet<string> diagnostics = new HashSet<string>();

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length != 2 && args.Length != 3)
            {
                PrintUsage();
                return 0;
            }

            string root = args[0];
            string reportFile = args[1];
            string configFile = null;

            if (args.Length == 3)
            {
                configFile = args[2];
            }

            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine("Specified root directory doesn't exist: " + root);
                return 1;
            }

            var files = GetFiles(root, configFile);

            new Checker().Check(files, reportFile);

            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: BinaryCompatChecker <root-folder> <output-report-file> [<config-file>]
    <root-folder>: root directory where to start searching for files
    <output-report-file>: where to write the output report
    <config-file>: (optional) a file with include/exclude patterns");
        }

        public static IEnumerable<string> GetFiles(string rootDirectory, string configFilePath, string pattern = "*.dll")
        {
            var list = new List<string>();
            IncludeExcludePattern includeExclude = null;

            if (File.Exists(configFilePath))
            {
                includeExclude = IncludeExcludePattern.ParseFromFile(configFilePath);
            }

            foreach (var file in Directory.GetFiles(rootDirectory, pattern, SearchOption.AllDirectories))
            {
                if (includeExclude == null || includeExclude.Includes(file))
                {
                    list.Add(file);
                }
            }

            return list;
        }

        public bool ShouldIncludeFile(string file)
        {
            throw new NotImplementedException();
        }

        public void Check(IEnumerable<string> files, string reportFile)
        {
            this.files = files;

            foreach (var file in files)
            {
                var assemblyDefinition = Load(file);
                if (assemblyDefinition == null)
                {
                    continue;
                }

                var references = assemblyDefinition.MainModule.AssemblyReferences;
                foreach (var reference in references)
                {
                    var resolved = Resolve(reference);
                    if (resolved == null)
                    {
                        unresolvedAssemblies.Add(reference.Name);
                        diagnostics.Add($"In assembly '{assemblyDefinition.Name.Name}': unable to resolve reference to '{reference.FullName}'");
                        continue;
                    }

                    Check(assemblyDefinition, resolved);
                }

                CheckMembers(assemblyDefinition);
            }

            foreach (var ex in diagnostics.OrderBy(s => s))
            {
                Log(ex);
            }

            if (sb.Length > 0)
            {
                var text = sb.ToString();
                File.WriteAllText(reportFile, text);
            }
        }

        public void Check(AssemblyDefinition referencing, AssemblyDefinition reference)
        {
            CheckTypes(referencing, reference);
        }

        private void CheckMembers(AssemblyDefinition assembly)
        {
            foreach (var memberReference in assembly.MainModule.GetMemberReferences())
            {
                try
                {
                    if (memberReference.DeclaringType.Scope.MetadataScopeType == MetadataScopeType.AssemblyNameReference && unresolvedAssemblies.Contains(memberReference.DeclaringType.Scope.Name))
                    {
                        // already reported an unresolved assembly; just ignore this one
                        continue;
                    }

                    var resolved = memberReference.Resolve();
                    if (resolved == null)
                    {
                        diagnostics.Add($"In assembly '{assembly.Name.Name}': Unable to resolve member reference '{memberReference.FullName}'");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"In assembly '{assembly.Name.Name}': {ex.Message}");
                }
            }
        }

        private void CheckTypes(AssemblyDefinition referencing, AssemblyDefinition reference)
        {
            foreach (var referencedType in referencing.MainModule.GetTypeReferences())
            {
                if (referencedType.Scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference || referencedType.Scope.Name != reference.Name.Name)
                {
                    continue;
                }

                var types = GetTypes(reference);
                if (!types.Contains(referencedType.FullName))
                {
                    diagnostics.Add($"In assembly '{referencing.Name.Name}': Unable to resolve type reference '{referencedType.FullName}' in '{reference.Name}'");
                }
            }
        }

        private HashSet<string> GetTypes(AssemblyDefinition assembly)
        {
            if (assemblyToTypeList.TryGetValue(assembly, out var types))
            {
                return types;
            }

            types = new HashSet<string>();
            assemblyToTypeList[assembly] = types;

            foreach (var topLevelType in assembly.MainModule.Types)
            {
                types.Add(topLevelType.FullName);
                AddNestedTypes(topLevelType, types);
            }

            foreach (var exportedType in assembly.MainModule.ExportedTypes)
            {
                types.Add(exportedType.FullName);
            }

            return types;
        }

        private void AddNestedTypes(TypeDefinition type, HashSet<string> types)
        {
            foreach (var nested in type.NestedTypes)
            {
                types.Add(nested.FullName);
                AddNestedTypes(nested, types);
            }
        }

        private void Log(string text)
        {
            sb.AppendLine(text);
        }

        private AssemblyDefinition Resolve(AssemblyNameReference reference)
        {
            foreach (var assemblyDefinition in filePathToModuleDefinition)
            {
                if (assemblyDefinition.Value.Name.Name == reference.Name ||
                    string.Equals(Path.GetFileNameWithoutExtension(assemblyDefinition.Key), reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return assemblyDefinition.Value;
                }
            }

            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return Load(file);
                }
            }

            try
            {
                var assembly = Assembly.Load(reference.FullName);
                return Load(assembly.Location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private AssemblyDefinition Load(string filePath)
        {
            if (!filePathToModuleDefinition.TryGetValue(filePath, out var assemblyDefinition))
            {
                try
                {
                    assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath);
                    filePathToModuleDefinition[filePath] = assemblyDefinition;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return assemblyDefinition;
        }
    }
}
