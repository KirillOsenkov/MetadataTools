using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public class Checker
    {
        Dictionary<AssemblyDefinition, HashSet<string>> assemblyToTypeList = new Dictionary<AssemblyDefinition, HashSet<string>>();
        List<string> reportLines = new List<string>();
        List<string> assembliesExamined = new List<string>();
        Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AssemblyDefinition> resolveCache = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        HashSet<string> unresolvedAssemblies = new HashSet<string>();
        HashSet<string> diagnostics = new HashSet<string>();

        [STAThread]
        static int Main(string[] args)
        {
            bool ignoreNetFrameworkAssemblies = false;

            // Parse parameterized args
            List<string> arguments = new List<string>(args);
            foreach (var arg in arguments.ToArray())
            {
                if (arg.Equals("/ignoreNetFx", StringComparison.OrdinalIgnoreCase))
                {
                    ignoreNetFrameworkAssemblies = true;
                    arguments.Remove(arg);
                }
            }

            args = arguments.ToArray();

            if (args.Length != 2 && args.Length != 3)
            {
                PrintUsage();
                return 0;
            }

            // Parse positional args
            string root = args[0];
            string reportFile = args[1];
            string configFile = null;

            if (args.Length == 3)
            {
                configFile = args[2];
            }

            if (!Directory.Exists(root) && !File.Exists(root))
            {
                Console.Error.WriteLine("Specified root directory or file doesn't exist: " + root);
                return 1;
            }

            var files = GetFiles(root, configFile, out var startFiles);

            bool success = new Checker().Check(root, files, startFiles, reportFile, ignoreNetFrameworkAssemblies);
            return success ? 0 : 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: BinaryCompatChecker [options] <root-folder> <output-report-file> [<config-file>]
    <root-folder|root-file>: root directory or root file where to start searching for files
    <output-report-file>: where to write the output report
    <config-file>: (optional) a file with include/exclude patterns
    Options:
        /ignoreNetFx: Ignores mismatches from framework assemblies");
        }

        public static IEnumerable<string> GetFiles(string rootDirectory, string configFilePath, out List<string> startFiles)
        {
            var list = new List<string>();
            startFiles = new List<string>();
            IncludeExcludePattern includeExclude = null;
            bool isRootFile = false;

            if (File.Exists(configFilePath))
            {
                includeExclude = IncludeExcludePattern.ParseFromFile(configFilePath);
            }

            if (File.Exists(rootDirectory))
            {
                isRootFile = true;
                startFiles.Add(rootDirectory);
                rootDirectory = Path.GetDirectoryName(rootDirectory);
            }

            foreach (var file in Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativeFilePath = file.Substring(rootDirectory.Length);

                if (file.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase))
                {
                    if (includeExclude != null && includeExclude.Includes(relativeFilePath))
                    {
                        list.Add(file);
                    }

                    continue;
                }

                if (includeExclude == null || !includeExclude.Excludes(relativeFilePath))
                {
                    if (PEFile.IsManagedAssembly(file))
                    {
                        list.Add(file);
                        if (!isRootFile)
                        {
                            startFiles.Add(file);
                        }
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
                RuntimeHelpers.EnsureSufficientExecutionStack(); // see https://github.com/KirillOsenkov/MetadataTools/issues/4
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

        private class VersionMismatch
        {
            public AssemblyDefinition Referencer;
            public AssemblyNameReference ExpectedReference;
            public AssemblyDefinition ActualAssembly;
        }

        private readonly List<VersionMismatch> versionMismatches
            = new List<VersionMismatch>();

        /// <returns>true if the check succeeded, false if the report is different from the baseline</returns>
        public bool Check(
            string rootDirectory, 
            IEnumerable<string> files, 
            IEnumerable<string> startFiles, 
            string reportFile, 
            bool ignoreFrameworkAssemblies = false)
        {
            this.files = files;
            var appConfigFiles = new List<string>();

            Queue<string> fileQueue = new Queue<string>(startFiles);
            HashSet<string> visitedFiles = new HashSet<string>(startFiles, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (file.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase))
                {
                    appConfigFiles.Add(file);
                    continue;
                }
            }

            while (fileQueue.Count != 0)
            {
                string file = fileQueue.Dequeue();

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
                    else
                    {
                        var resolvedPath = resolved.MainModule.FileName;
                        if (resolvedPath != null && visitedFiles.Add(resolvedPath))
                        {
                            fileQueue.Enqueue(resolvedPath);
                        }
                    }

                    Check(assemblyDefinition, resolved, reference, ignoreFrameworkAssemblies);
                }

                CheckMembers(assemblyDefinition);
            }

            CheckAppConfigFiles(appConfigFiles);

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
 The current assembly binary compatibility report is different from the checked-in baseline.
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

                ListExaminedAssemblies(reportFile);
            }

            return true;
        }

        private void ListExaminedAssemblies(string reportFile)
        {
            string filePath = Path.ChangeExtension(reportFile, ".report.txt");
            assembliesExamined.Sort();
            File.WriteAllLines(filePath, assembliesExamined);
        }

        private void CheckAppConfigFiles(IEnumerable<string> appConfigFiles)
        {
            var versionMismatchesByName = versionMismatches
                .ToLookup(mismatch => mismatch.ExpectedReference.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var appConfigFilePath in appConfigFiles)
            {
                var appConfigFileName = Path.GetFileName(appConfigFilePath);
                var appConfigFile = AppConfigFile.Read(appConfigFilePath);
                if (appConfigFile.Errors.Any())
                {
                    foreach (var error in appConfigFile.Errors)
                    {
                        diagnostics.Add($"In app.config file {appConfigFileName}: {error}");
                    }

                    continue;
                }

                foreach (var bindingRedirect in appConfigFile.BindingRedirects)
                {
                    CheckBindingRedirect(
                        appConfigFileName,
                        bindingRedirect.Name,
                        bindingRedirect.PublicKeyToken,
                        bindingRedirect.OldVersionRangeStart,
                        bindingRedirect.OldVersionRangeEnd,
                        bindingRedirect.NewVersion,
                        versionMismatchesByName);
                }
            }

            foreach (var versionMismatch in versionMismatchesByName.Values.SelectMany(list => list))
            {
                diagnostics.Add($"Assembly {versionMismatch.Referencer.Name.Name} is referencing {versionMismatch.ExpectedReference.FullName} but found {versionMismatch.ActualAssembly.FullName} at {versionMismatch.ActualAssembly.MainModule.FileName}");
            }
        }

        private void CheckBindingRedirect(
            string appConfigFileName,
            string name,
            string publicKeyToken,
            Version oldVersionStart,
            Version oldVersionEnd,
            Version newVersion,
            Dictionary<string, List<VersionMismatch>> versionMismatchesByName)
        {
            bool foundNewVersion = false;
            var foundVersions = new List<Version>();

            foreach (var kvp in this.filePathToModuleDefinition)
            {
                var assembly = kvp.Value;
                if (!string.Equals(assembly.Name?.Name, name))
                {
                    continue;
                }

                foundVersions.Add(assembly.Name.Version);

                if (assembly.Name.Version == newVersion)
                {
                    foundNewVersion = true;
                    var actualToken = BitConverter.ToString(assembly.Name.PublicKeyToken).Replace("-", "").ToLowerInvariant();
                    if (!string.Equals(actualToken, publicKeyToken, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add($"In {appConfigFileName}: publicKeyToken {publicKeyToken} from bindingRedirect for {name} doesn't match actual assembly {actualToken}");
                    }

                    continue;
                }

                if (assembly.Name.Version < oldVersionStart)
                {
                    diagnostics.Add($"In {appConfigFileName}: {assembly.FullName} version is less than bindingRedirect range start {oldVersionStart}");
                    continue;
                }

                if (assembly.Name.Version > oldVersionEnd)
                {
                    diagnostics.Add($"In {appConfigFileName}: {assembly.FullName} version is higher than bindingRedirect range end {oldVersionEnd}");
                    continue;
                }
            }

            if (versionMismatchesByName.TryGetValue(name, out var mismatches))
            {
                versionMismatchesByName.Remove(name);
                foreach (var versionMismatch in mismatches.ToArray())
                {
                    var actualVersion = versionMismatch.ActualAssembly.Name.Version;
                    if (actualVersion != newVersion)
                    {
                        if (actualVersion < oldVersionStart)
                        {
                            diagnostics.Add($"In {appConfigFileName}: {versionMismatch.ActualAssembly.FullName} version is less than bindingRedirect range start {oldVersionStart} (Expected by {versionMismatch.Referencer.Name})");
                            continue;
                        }

                        if (actualVersion > oldVersionEnd)
                        {
                            diagnostics.Add($"In {appConfigFileName}: {versionMismatch.ActualAssembly.FullName} version is higher than bindingRedirect range end {oldVersionEnd} (Expected by {versionMismatch.Referencer.Name})");
                            continue;
                        }
                    }
                }
            }

            if (!foundNewVersion)
            {
                var message = $"In {appConfigFileName}: couldn't find assembly '{name}' with version {newVersion}.";
                if (foundVersions.Count > 0)
                {
                    message += $" Found versions: {string.Join(",", foundVersions.Select(v => v.ToString()).Distinct())}";
                }

                diagnostics.Add(message);
            }
        }

        private void OutputDiff(IEnumerable<string> baseline, IEnumerable<string> reportLines)
        {
            var removed = baseline.Except(reportLines);
            var added = reportLines.Except(baseline);

            if (removed.Any())
            {
                OutputError("=================================");
                OutputError("These expected lines are missing:");
                foreach (var removedLine in removed)
                {
                    OutputError(removedLine);
                }

                OutputError("=================================");
            }

            if (added.Any())
            {
                OutputError("=================================");
                OutputError("These actual lines are new:");
                foreach (var addedLine in added)
                {
                    OutputError(addedLine);
                }

                OutputError("=================================");
            }
        }

        private void OutputError(string text)
        {
            Console.Error.WriteLine(text);
        }

        /// <summary>
        /// Returns true if the <paramref name="assembly"/> is .NET Framework assembly.
        /// </summary>
        private static bool IsNetFrameworkAssembly(AssemblyDefinition assembly)
        {
            // Hacky way of detecting it.
            return assembly
                .CustomAttributes
                .FirstOrDefault(a => 
                    a.AttributeType.Name == "AssemblyProductAttribute" && 
                    a.ConstructorArguments != null && 
                    a.ConstructorArguments.FirstOrDefault(c => c.Value.ToString() == "Microsoft® .NET Framework").Value != null) != null;
        }

        /// <summary>
        /// Returns true if the <paramref name="assembly"/> is a facade assembly with type forwarders only.
        /// </summary>
        private static bool IsFacadeAssembly(AssemblyDefinition assembly)
        {
            return false;
        }

        public void Check(AssemblyDefinition referencing, AssemblyDefinition referenced, AssemblyNameReference reference, bool ignoreFrameworkAssemblies)
        {
            if (!ignoreFrameworkAssemblies || !IsNetFrameworkAssembly(referenced))
            {
                if (reference.Version != referenced.Name.Version)
                {
                    //diagnostics.Add($"Assembly {referencing.Name.FullName} is referencing {reference.FullName} but found {referenced.FullName}");
                    versionMismatches.Add(new VersionMismatch()
                    {
                        Referencer = referencing,
                        ExpectedReference = reference,
                        ActualAssembly = referenced
                    });
                }
            }

            CheckTypes(referencing, referenced);
        }

        private void CheckMembers(AssemblyDefinition assembly)
        {
            foreach (var memberReference in assembly.MainModule.GetMemberReferences())
            {
                try
                {
                    if (memberReference.DeclaringType.IsArray)
                    {
                        continue;
                    }

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
                        AssemblyResolver = this.resolver,
                        InMemory = true
                    };
                    assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath, readerParameters);
                    filePathToModuleDefinition[filePath] = assemblyDefinition;
                    assembliesExamined.Add($"{filePath}; {assemblyDefinition.FullName}");
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
