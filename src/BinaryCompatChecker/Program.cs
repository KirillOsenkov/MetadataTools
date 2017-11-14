using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public class Checker
    {
        Dictionary<AssemblyDefinition, HashSet<string>> assemblyToTypeList = new Dictionary<AssemblyDefinition, HashSet<string>>();
        List<string> reportLines = new List<string>();
        Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AssemblyDefinition> resolveCache = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
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

            bool success = new Checker().Check(root, files, reportFile);
            return success ? 0 : 1;
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
                var relativeFilePath = file.Substring(rootDirectory.Length);

                if (includeExclude == null || !includeExclude.Excludes(relativeFilePath))
                {
                    if (PEFile.IsManagedAssembly(file))
                    {
                        list.Add(file);
                    }
                }
            }

            return list;
        }

        public class CustomAssemblyResolver : BaseAssemblyResolver
        {
            private readonly Checker checker;

            public CustomAssemblyResolver(Checker checker)
            {
                this.checker = checker;
            }

            public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
            {
                var resolved = checker.Resolve(name);
                resolved = resolved ?? base.Resolve(name, parameters);
                return resolved;
            }
        }

        IAssemblyResolver resolver;

        public Checker()
        {
            resolver = new CustomAssemblyResolver(this);
        }

        /// <returns>true if the check succeeded, false if the report is different from the baseline</returns>
        public bool Check(string rootDirectory, IEnumerable<string> files, string reportFile)
        {
            this.files = files;

            foreach (var file in files)
            {
                var assemblyDefinition = Load(file);
                if (assemblyDefinition == null)
                {
                    continue;
                }

                // var relativePath = file.Substring(rootDirectory.Length + 1);
                // Log($"Assembly: {relativePath}: {assemblyDefinition.FullName}");

                var references = assemblyDefinition.MainModule.AssemblyReferences;
                foreach (var reference in references)
                {
                    var resolved = Resolve(reference);
                    if (resolved == null)
                    {
                        unresolvedAssemblies.Add(reference.Name);
                        diagnostics.Add($"In assembly '{assemblyDefinition.Name.FullName}': unable to resolve reference to '{reference.FullName}'");
                        continue;
                    }

                    Check(assemblyDefinition, resolved);
                }

                CheckMembers(assemblyDefinition);
            }

            Dispose();

            foreach (var ex in diagnostics.OrderBy(s => s))
            {
                Log(ex);
            }

            if (reportLines.Count > 0)
            {
                if (!File.Exists(reportFile))
                {
                    // initial baseline creation mode
                    File.WriteAllLines(reportFile, reportLines);
                }
                else
                {
                    var baseline = File.ReadAllLines(reportFile);
                    if (!Enumerable.SequenceEqual(baseline, reportLines))
                    {
                        OutputError(@"BinaryCompatChecker failed.
 The current assembly binary compatibility report is different from the checked in baseline.
 Baseline file: " + reportFile);
                        OutputDiff(baseline, reportLines);
                        try
                        {
                            File.WriteAllLines(reportFile, reportLines);
                        }
                        catch (Exception)
                        {
                        }

                        return false;
                    }
                }
            }

            return true;
        }

        private void Dispose()
        {
            foreach (var kvp in this.filePathToModuleDefinition)
            {
                kvp.Value.Dispose();
            }
        }

        private void OutputDiff(IEnumerable<string> baseline, IEnumerable<string> reportLines)
        {
            var removed = baseline.Except(reportLines);
            var added = reportLines.Except(baseline);

            if (removed.Any())
            {
                OutputError("These expected lines are missing:");
                foreach (var removedLine in removed)
                {
                    OutputError(removedLine);
                }
            }

            if (added.Any())
            {
                OutputError("These actual lines are new:");
                foreach (var addedLine in added)
                {
                    OutputError(addedLine);
                }
            }
        }

        private void OutputError(string text)
        {
            Console.Error.WriteLine(text);
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
                    var scope = memberReference.DeclaringType.Scope;
                    string referenceToAssembly = scope?.Name;

                    if (referenceToAssembly != null && unresolvedAssemblies.Contains(referenceToAssembly))
                    {
                        // already reported an unresolved assembly; just ignore this one
                        continue;
                    }

                    if (scope is AssemblyNameReference assemblyNameReference)
                    {
                        referenceToAssembly = assemblyNameReference.FullName;
                    }

                    var resolved = memberReference.Resolve();
                    if (resolved == null)
                    {
                        diagnostics.Add($"In assembly '{assembly.Name.FullName}': Unable to resolve member reference '{memberReference.FullName}' from assembly '{referenceToAssembly}'");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"In assembly '{assembly.Name.FullName}': {ex.Message}");
                }
            }
        }

        private void CheckTypes(AssemblyDefinition referencing, AssemblyDefinition reference)
        {
            foreach (var referencedType in referencing.MainModule.GetTypeReferences())
            {
                if (referencedType.Scope == null ||
                    referencedType.Scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference ||
                    referencedType.Scope.Name != reference.Name.Name)
                {
                    continue;
                }

                var types = GetTypes(reference);
                if (!types.Contains(referencedType.FullName))
                {
                    diagnostics.Add($"In assembly '{referencing.Name.FullName}': Unable to resolve type reference '{referencedType.FullName}' in '{reference.Name}'");
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
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            reportLines.Add(text);
        }

        private AssemblyDefinition Resolve(AssemblyNameReference reference)
        {
            AssemblyDefinition result = null;
            if (resolveCache.TryGetValue(reference.FullName, out result))
            {
                return result;
            }

            foreach (var assemblyDefinition in filePathToModuleDefinition)
            {
                if (assemblyDefinition.Value.Name.FullName == reference.FullName ||
                    string.Equals(Path.GetFileNameWithoutExtension(assemblyDefinition.Key), reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result = assemblyDefinition.Value;
                    resolveCache[reference.FullName] = result;
                    return result;
                }
            }

            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    result = Load(file);
                    resolveCache[reference.FullName] = result;
                    return result;
                }
            }

            try
            {
                var assembly = Assembly.Load(reference.FullName);
                result = Load(assembly.Location);
                resolveCache[reference.FullName] = result;
                return result;
            }
            catch (Exception ex)
            {
                resolveCache[reference.FullName] = null;
                diagnostics.Add(ex.Message);
                return null;
            }
        }

        private AssemblyDefinition Load(string filePath)
        {
            if (!PEFile.IsManagedAssembly(filePath))
            {
                return null;
            }

            if (!filePathToModuleDefinition.TryGetValue(filePath, out var assemblyDefinition))
            {
                try
                {
                    var readerParameters = new ReaderParameters
                    {
                        AssemblyResolver = this.resolver
                    };
                    assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath, readerParameters);
                    filePathToModuleDefinition[filePath] = assemblyDefinition;
                }
                catch (Exception ex)
                {
                    diagnostics.Add(ex.ToString());
                    return null;
                }
            }

            return assemblyDefinition;
        }
    }
}
