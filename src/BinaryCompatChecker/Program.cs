using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public class Checker
    {
        Dictionary<AssemblyDefinition, Dictionary<string, bool>> assemblyToTypeList = new Dictionary<AssemblyDefinition, Dictionary<string, bool>>();
        List<string> reportLines = new List<string>();
        List<string> assembliesExamined = new List<string>();
        List<IVTUsage> ivtUsages = new List<IVTUsage>();
        Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AssemblyDefinition> resolveCache = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        private string rootDirectory;
        HashSet<string> unresolvedAssemblies = new HashSet<string>();
        HashSet<string> diagnostics = new HashSet<string>();

        public class IVTUsage
        {
            public string ExposingAssembly { get; set; }
            public string ConsumingAssembly { get; set; }
            public string Member { get; set; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool ignoreNetFrameworkAssemblies = true;

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
                startFiles.Add(Path.GetFullPath(rootDirectory));
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
                        list.Add(Path.GetFullPath(file));
                    }

                    continue;
                }

                if (includeExclude == null || !includeExclude.Excludes(relativeFilePath))
                {
                    if (PEFile.IsManagedAssembly(file))
                    {
                        var filePath = Path.GetFullPath(file);
                        list.Add(filePath);
                        if (!isRootFile)
                        {
                            startFiles.Add(filePath);
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
            bool success = true;

            this.files = files;
            this.rootDirectory = rootDirectory;
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

                        success = false;
                    }
                }

                ListExaminedAssemblies(reportFile);
            }

            WriteIVTReport(reportFile);

            //WriteIVTReport(
            //    reportFile,
            //    ".ivt.roslyn.txt",
            //    u => IsRoslynAssembly(u.ExposingAssembly) && !IsRoslynAssembly(u.ConsumingAssembly));

            return success;
        }

        private static bool IsRoslynAssembly(string assemblyName)
        {
            if (assemblyName.Contains("Microsoft.CodeAnalysis") || assemblyName.Contains("VisualStudio.LanguageServices"))
            {
                return true;
            }

            return false;
        }

        private void WriteIVTReport(string primaryReportFile, string fileName = ".ivt.txt", Func<IVTUsage, bool> usageFilter = null)
        {
            string filePath = Path.ChangeExtension(primaryReportFile, fileName);
            var sb = new StringBuilder();

            var usages = ivtUsages
                .Where(u => !IsNetFrameworkAssembly(u.ConsumingAssembly) && !IsNetFrameworkAssembly(u.ExposingAssembly));

            if (usageFilter != null)
            {
                usages = usages.Where(u => usageFilter(u));
            }

            if (!usages.Any())
            {
                return;
            }

            foreach (var exposingAssembly in usages
                .GroupBy(u => u.ExposingAssembly)
                .OrderBy(g => g.Key))
            {
                sb.AppendLine($"{exposingAssembly.Key}");
                sb.AppendLine($"{new string('=', exposingAssembly.Key.Length)}");
                foreach (var consumingAssembly in exposingAssembly.GroupBy(u => u.ConsumingAssembly).OrderBy(g => g.Key))
                {
                    sb.AppendLine($"  Consumed in: {consumingAssembly.Key}");
                    foreach (var ivt in consumingAssembly.Select(s => s.Member).Distinct().OrderBy(s => s))
                    {
                        sb.AppendLine("    " + ivt);
                    }

                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            if (sb.Length > 0)
            {
                File.WriteAllText(filePath, sb.ToString());
            }
        }

        private void ListExaminedAssemblies(string reportFile)
        {
            string filePath = Path.ChangeExtension(reportFile, ".assemblylist.txt");
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
                string referencedFullName = versionMismatch.ExpectedReference.FullName;
                if (referencedFullName.StartsWith("netstandard,"))
                {
                    continue;
                }

                string actualFilePath = versionMismatch.ActualAssembly.MainModule.FileName;
                if (actualFilePath.Contains("Mono.framework"))
                {
                    continue;
                }

                if (actualFilePath.StartsWith(rootDirectory))
                {
                    actualFilePath = actualFilePath.Substring(rootDirectory.Length);
                    if (actualFilePath.StartsWith("\\") || actualFilePath.StartsWith("/"))
                    {
                        actualFilePath = actualFilePath.Substring(1);
                    }
                }

                diagnostics.Add($"Assembly {versionMismatch.Referencer.Name.Name} is referencing {referencedFullName} but found {versionMismatch.ActualAssembly.FullName} at {actualFilePath}");
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
                    if (string.IsNullOrEmpty(actualToken))
                    {
                        actualToken = "null";
                    }

                    if (!string.Equals(actualToken, publicKeyToken, StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add($"In {appConfigFileName}: publicKeyToken '{publicKeyToken}' from bindingRedirect for {name} doesn't match one from the actual assembly: '{actualToken}'");
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

        private static Dictionary<string, bool> frameworkAssemblyNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private static bool IsNetFrameworkAssembly(string assemblyName)
        {
            frameworkAssemblyNames.TryGetValue(assemblyName, out bool result);
            return result;
        }

        /// <summary>
        /// Returns true if the <paramref name="assembly"/> is .NET Framework assembly.
        /// </summary>
        private static bool IsNetFrameworkAssembly(AssemblyDefinition assembly)
        {
            string key = assembly.MainModule.FileName;
            if (frameworkAssemblyNames.TryGetValue(key, out bool result))
            {
                return result;  
            }

            // Hacky way of detecting it.
            result = assembly
                .CustomAttributes
                .FirstOrDefault(a => 
                    a.AttributeType.Name == "AssemblyProductAttribute" && 
                    a.ConstructorArguments != null && 
                    a.ConstructorArguments.FirstOrDefault(c => c.Value.ToString() == "Microsoft® .NET Framework").Value != null) != null;
            frameworkAssemblyNames[key] = result;
            return result;
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
                    else
                    {
                        var ivtUsage = TryGetIVTUsage(memberReference, resolved);
                        if (ivtUsage != null)
                        {
                            ivtUsages.Add(ivtUsage);
                        }
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"In assembly '{assembly.Name.FullName}': {ex.Message}");
                }
            }
        }

        private IVTUsage TryGetIVTUsage(MemberReference memberReference, IMemberDefinition definition)
        {
            string consumingAssembly = memberReference.Module.FileName;

            if (definition is MethodDefinition methodDefinition)
            {
                if (consumingAssembly == methodDefinition.Module.FileName)
                {
                    return null;
                }

                if (AllPublic(methodDefinition))
                {
                    return null;
                }

                return new IVTUsage
                {
                    ExposingAssembly = methodDefinition.Module.FileName,
                    ConsumingAssembly = consumingAssembly,
                    Member = methodDefinition.ToString()
                };
            }
            else if (definition is FieldDefinition fieldDefinition)
            {
                if (consumingAssembly == fieldDefinition.Module.FileName)
                {
                    return null;
                }

                if (AllPublic(fieldDefinition))
                {
                    return null;
                }

                return new IVTUsage
                {
                    ExposingAssembly = fieldDefinition.Module.FileName,
                    ConsumingAssembly = consumingAssembly,
                    Member = fieldDefinition.ToString()
                };
            }

            return null;
        }

        private bool AllPublic(FieldDefinition field)
        {
            if (!field.IsPublic)
            {
                return false;
            }

            var type = field.DeclaringType;
            return AllPublic(type);
        }

        private bool AllPublic(MethodDefinition method)
        {
            if (!method.IsPublic)
            {
                return false;
            }

            var type = method.DeclaringType;
            return AllPublic(type);
        }

        private static bool AllPublic(TypeDefinition type)
        {
            while (type != null)
            {
                if (!type.IsPublic && !type.IsNestedPublic)
                {
                    return false;
                }

                type = type.DeclaringType;
            }

            return true;
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
                if (types.TryGetValue(referencedType.FullName, out bool isPublic))
                {
                    if (!isPublic)
                    {
                        var ivtUsage = new IVTUsage
                        {
                            ConsumingAssembly = referencing.MainModule.FileName,
                            ExposingAssembly = reference.MainModule.FileName,
                            Member = referencedType.FullName
                        };
                        ivtUsages.Add(ivtUsage);
                    }
                }
                else
                {
                    diagnostics.Add($"In assembly '{referencing.Name.FullName}': Unable to resolve type reference '{referencedType.FullName}' in '{reference.Name}'");
                }
            }
        }

        private Dictionary<string, bool> GetTypes(AssemblyDefinition assembly)
        {
            if (assemblyToTypeList.TryGetValue(assembly, out var types))
            {
                return types;
            }

            types = new Dictionary<string, bool>();
            assemblyToTypeList[assembly] = types;

            foreach (var topLevelType in assembly.MainModule.Types)
            {
                types.Add(topLevelType.FullName, topLevelType.IsPublic);
                AddNestedTypes(topLevelType, types);
            }

            foreach (var exportedType in assembly.MainModule.ExportedTypes)
            {
                types.Add(exportedType.FullName, exportedType.IsPublic);
            }

            return types;
        }

        private void AddNestedTypes(TypeDefinition type, Dictionary<string, bool> types)
        {
            foreach (var nested in type.NestedTypes)
            {
                types.Add(nested.FullName, nested.IsPublic);
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
