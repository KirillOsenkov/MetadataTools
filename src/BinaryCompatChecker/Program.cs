using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Mono.Cecil;
using Mono.Collections.Generic;

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
        HashSet<string> unresolvedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> diagnostics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, bool> frameworkAssemblyNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static bool CallAssemblyLoadToResolveAssemblies { get; set; }
        public static bool ReportEmbeddedInteropTypes { get; set; } = true;
        public static bool IgnoreNetFrameworkAssemblies { get; set; }
        public static bool ReportVersionMismatch { get; set; } = true;
        public static bool ReportIntPtrConstructors { get; set; }

        public class IVTUsage
        {
            public string ExposingAssembly { get; set; }
            public string ConsumingAssembly { get; set; }
            public string Member { get; set; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // Parse parameterized args
            var arguments = new List<string>(args);
            foreach (var arg in arguments.ToArray())
            {
                if (arg.Equals("/ignoreNetFx", StringComparison.OrdinalIgnoreCase))
                {
                    IgnoreNetFrameworkAssemblies = true;
                    arguments.Remove(arg);
                }

                if (arg.Equals("/ignoreVersionMismatch", StringComparison.OrdinalIgnoreCase))
                {
                    ReportVersionMismatch = false;
                    arguments.Remove(arg);
                }

                if (arg.Equals("/ignoreEmbeddedInteropTypes", StringComparison.OrdinalIgnoreCase))
                {
                    ReportEmbeddedInteropTypes = false;
                    arguments.Remove(arg);
                }

                if (arg.Equals("/assemblyLoad", StringComparison.OrdinalIgnoreCase))
                {
                    CallAssemblyLoadToResolveAssemblies = true;
                    arguments.Remove(arg);

                    if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                    {
                        AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
                    }
                }

                if (arg.Equals("/intPtrCtors", StringComparison.OrdinalIgnoreCase))
                {
                    ReportIntPtrConstructors = true;
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

            bool success = new Checker().Check(
                root,
                files,
                startFiles,
                reportFile);
            return success ? 0 : 1;
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (args.Name.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin";
                candidate = Path.Combine(candidate, args.Name.Substring(0, args.Name.IndexOf(',')) + ".dll");
                if (File.Exists(candidate))
                {
                    return Assembly.Load(AssemblyName.GetAssemblyName(candidate));
                }
            }
            else if (args.Name.StartsWith("NuGet.", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\NuGet";
                candidate = Path.Combine(candidate, args.Name.Substring(0, args.Name.IndexOf(',')) + ".dll");
                if (File.Exists(candidate))
                {
                    return Assembly.Load(AssemblyName.GetAssemblyName(candidate));
                }
            }

            return null;
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
            string reportFile)
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
                        diagnostics.Add($"In assembly '{assemblyDefinition.Name.FullName}': Failed to resolve assembly reference to '{reference.FullName}'");

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

                    Check(assemblyDefinition, resolved, reference);
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

            WriteIVTReport(
                reportFile,
                ".ivt.roslyn.txt",
                u => IsRoslynAssembly(u.ExposingAssembly) && !IsRoslynAssembly(u.ConsumingAssembly));

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

            if (ReportVersionMismatch)
            {
                ReportVersionMismatches(versionMismatchesByName);
            }
        }

        private void ReportVersionMismatches(Dictionary<string, List<VersionMismatch>> versionMismatchesByName)
        {
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
                .FirstOrDefault(a => IsAssemblyProductFramework(a) || IsAssemblyMetadataFramework(a)) != null;
            frameworkAssemblyNames[key] = result;
            return result;
        }

        private static bool IsAssemblyMetadataFramework(CustomAttribute a)
        {
            return
                a.AttributeType.Name == "AssemblyMetadataAttribute" &&
                a.ConstructorArguments != null &&
                a.ConstructorArguments.Count > 0 &&
                a.ConstructorArguments[0].Value.ToString() == ".NETFrameworkAssembly";
        }

        private static bool IsAssemblyProductFramework(CustomAttribute a)
        {
            return
                a.AttributeType.Name == "AssemblyProductAttribute" &&
                a.ConstructorArguments != null &&
                a.ConstructorArguments.FirstOrDefault(c => c.Value.ToString() == "Microsoft® .NET Framework").Value != null;
        }

        /// <summary>
        /// Returns true if the <paramref name="assembly"/> is a facade assembly with type forwarders only.
        /// </summary>
        private static bool IsFacadeAssembly(AssemblyDefinition assembly)
        {
            return false;
        }

        public void Check(
            AssemblyDefinition referencing,
            AssemblyDefinition referenced,
            AssemblyNameReference reference)
        {
            if (!IgnoreNetFrameworkAssemblies || !IsNetFrameworkAssembly(referenced))
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

        public static MethodDefinition GetMethod(Collection<MethodDefinition> methods, MethodReference reference)
        {
            for (int i = 0; i < methods.Count; i++)
            {
                var method = methods[i];

                if (method.HasOverrides)
                {
                    foreach (var overrideMethod in method.Overrides)
                    {
                        if (AreSame(overrideMethod, reference))
                        {
                            return method;
                        }
                    }
                }

                string methodName = method.Name;
                int dot = methodName.LastIndexOf('.');
                if (dot > 0)
                {
                    methodName = methodName.Substring(dot + 1);
                }

                if (methodName != reference.Name)
                    continue;

                if (method.HasGenericParameters != reference.HasGenericParameters)
                    continue;

                if (method.HasGenericParameters && method.GenericParameters.Count != reference.GenericParameters.Count)
                    continue;

                if (!AreSame(method.ReturnType, reference.ReturnType))
                    continue;

                if (IsVarArg(method) != IsVarArg(reference))
                    continue;

                if (IsVarArg(method) && IsVarArgCallTo(method, reference))
                    return method;

                if (method.HasParameters != reference.HasParameters)
                    continue;

                if (!method.HasParameters && !reference.HasParameters)
                    return method;

                if (!AreSame(method.Parameters, reference.Parameters))
                    continue;

                return method;
            }

            return null;
        }

        public static bool AreSame(MethodReference method, MethodReference reference)
        {
            if (method.Name != reference.Name)
                return false;

            if (method.HasGenericParameters != reference.HasGenericParameters)
                return false;

            if (method.HasGenericParameters && method.GenericParameters.Count != reference.GenericParameters.Count)
                return false;

            if (!AreSame(method.ReturnType, reference.ReturnType))
                return false;

            if (IsVarArg(method) != IsVarArg(reference))
                return false;

            if (IsVarArg(method) && IsVarArgCallTo(method, reference))
                return true;

            if (method.HasParameters != reference.HasParameters)
                return false;

            if (!method.HasParameters && !reference.HasParameters)
                return true;

            if (!AreSame(method.Parameters, reference.Parameters))
                return false;

            return true;
        }

        public static bool IsVarArg(IMethodSignature self)
        {
            return self.CallingConvention == MethodCallingConvention.VarArg;
        }

        static bool AreSame(Collection<ParameterDefinition> a, Collection<ParameterDefinition> b)
        {
            var count = a.Count;

            if (count != b.Count)
            {
                return false;
            }

            if (count == 0)
            {
                return true;
            }

            for (int i = 0; i < count; i++)
            {
                if (!AreSame(a[i].ParameterType, b[i].ParameterType))
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsVarArgCallTo(MethodReference method, MethodReference reference)
        {
            var methodParameters = method.Parameters;
            var referenceParameters = reference.Parameters;

            if (methodParameters.Count >= referenceParameters.Count)
            {
                return false;
            }

            if (GetSentinelPosition(reference) != methodParameters.Count)
            {
                return false;
            }

            for (int i = 0; i < methodParameters.Count; i++)
            {
                if (!AreSame(methodParameters[i].ParameterType, referenceParameters[i].ParameterType))
                {
                    return false;
                }
            }

            return true;
        }

        public static int GetSentinelPosition(IMethodSignature self)
        {
            if (!self.HasParameters)
            {
                return -1;
            }

            var parameters = self.Parameters;
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].ParameterType.IsSentinel)
                {
                    return i;
                }
            }

            return -1;
        }

        static bool AreSame(TypeSpecification a, TypeSpecification b)
        {
            if (!AreSame(a.ElementType, b.ElementType))
            {
                return false;
            }

            if (a.IsGenericInstance)
            {
                return AreSame((GenericInstanceType)a, (GenericInstanceType)b);
            }

            if (a.IsRequiredModifier || a.IsOptionalModifier)
            {
                return AreSame((IModifierType)a, (IModifierType)b);
            }

            if (a.IsArray)
            {
                return AreSame((ArrayType)a, (ArrayType)b);
            }

            return true;
        }

        static bool AreSame(ArrayType a, ArrayType b)
        {
            if (a.Rank != b.Rank)
            {
                return false;
            }

            // TODO: dimensions

            return true;
        }

        static bool AreSame(IModifierType a, IModifierType b)
        {
            return AreSame(a.ModifierType, b.ModifierType);
        }

        static bool AreSame(GenericInstanceType a, GenericInstanceType b)
        {
            if (a.GenericArguments.Count != b.GenericArguments.Count)
            {
                return false;
            }

            for (int i = 0; i < a.GenericArguments.Count; i++)
            {
                if (!AreSame(a.GenericArguments[i], b.GenericArguments[i]))
                {
                    return false;
                }
            }

            return true;
        }

        static bool AreSame(GenericParameter a, GenericParameter b)
        {
            return a.Position == b.Position;
        }

        static bool AreSame(TypeReference a, TypeReference b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a == null || b == null)
            {
                return false;
            }

            if (a.MetadataType != b.MetadataType)
            {
                return false;
            }

            if (a.IsGenericParameter)
            {
                return AreSame((GenericParameter)a, (GenericParameter)b);
            }

            if (IsTypeSpecification(a))
            {
                return AreSame((TypeSpecification)a, (TypeSpecification)b);
            }

            if (a.Name != b.Name || a.Namespace != b.Namespace)
            {
                return false;
            }

            //TODO: check scope

            return AreSame(a.DeclaringType, b.DeclaringType);
        }

        public static bool IsTypeSpecification(TypeReference type)
        {
            switch ((ElementType)type.MetadataType)
            {
                case ElementType.Array:
                case ElementType.ByRef:
                case ElementType.CModOpt:
                case ElementType.CModReqD:
                case ElementType.FnPtr:
                case ElementType.GenericInst:
                case ElementType.MVar:
                case ElementType.Pinned:
                case ElementType.Ptr:
                case ElementType.SzArray:
                case ElementType.Sentinel:
                case ElementType.Var:
                    return true;
            }

            return false;
        }

        enum ElementType : byte
        {
            None = 0x00,
            Void = 0x01,
            Boolean = 0x02,
            Char = 0x03,
            I1 = 0x04,
            U1 = 0x05,
            I2 = 0x06,
            U2 = 0x07,
            I4 = 0x08,
            U4 = 0x09,
            I8 = 0x0a,
            U8 = 0x0b,
            R4 = 0x0c,
            R8 = 0x0d,
            String = 0x0e,
            Ptr = 0x0f,   // Followed by <type> token
            ByRef = 0x10,   // Followed by <type> token
            ValueType = 0x11,   // Followed by <type> token
            Class = 0x12,   // Followed by <type> token
            Var = 0x13,   // Followed by generic parameter number
            Array = 0x14,   // <type> <rank> <boundsCount> <bound1>  <loCount> <lo1>
            GenericInst = 0x15,   // <type> <type-arg-count> <type-1> ... <type-n> */
            TypedByRef = 0x16,
            I = 0x18,   // System.IntPtr
            U = 0x19,   // System.UIntPtr
            FnPtr = 0x1b,   // Followed by full method signature
            Object = 0x1c,   // System.Object
            SzArray = 0x1d,   // Single-dim array with 0 lower bound
            MVar = 0x1e,   // Followed by generic parameter number
            CModReqD = 0x1f,   // Required modifier : followed by a TypeDef or TypeRef token
            CModOpt = 0x20,   // Optional modifier : followed by a TypeDef or TypeRef token
            Internal = 0x21,   // Implemented within the CLI
            Modifier = 0x40,   // Or'd with following element types
            Sentinel = 0x41,   // Sentinel for varargs method signature
            Pinned = 0x45,   // Denotes a local variable that points at a pinned object

            // special undocumented constants
            Type = 0x50,
            Boxed = 0x51,
            Enum = 0x55
        }

        static TypeDefinition ResolveBaseType(TypeDefinition type)
        {
            if (type == null)
            {
                return null;
            }

            var baseType = type.BaseType;
            if (baseType == null)
            {
                return null;
            }

            return baseType.Resolve();
        }

        public static MethodDefinition FindInterfaceMethodImplementation(TypeDefinition typeDefinition, MethodDefinition interfaceMethod, ref bool sawGenerics)
        {
            if (typeDefinition.HasGenericParameters)
            {
                sawGenerics = true;
                return null;
            }

            var matching = GetMethod(typeDefinition.Methods, interfaceMethod);
            if (matching != null)
            {
                return matching;
            }

            var baseType = ResolveBaseType(typeDefinition);
            if (baseType != null)
            {
                return FindInterfaceMethodImplementation(baseType, interfaceMethod, ref sawGenerics);
            }

            return null;
        }

        public static MethodDefinition GetBaseMethod(MethodDefinition methodDefinition)
        {
            if (methodDefinition == null)
            {
                throw new ArgumentNullException("methodDefinition");
            }

            if (!methodDefinition.IsVirtual)
            {
                return null;
            }

            if (methodDefinition.IsNewSlot)
            {
                return null;
            }

            try
            {
                var baseType = ResolveBaseType(methodDefinition.DeclaringType);
                while (baseType != null)
                {
                    var baseMethod = MetadataResolver.GetMethod(baseType.Methods, methodDefinition);
                    if (baseMethod != null)
                    {
                        return baseMethod;
                    }

                    baseType = ResolveBaseType(baseType);
                }
            }
            catch
            {
            }

            return null;
        }

        private void CheckTypeDefinitions(string assemblyFullName, ModuleDefinition module, List<MemberReference> references)
        {
            // Do not search the Xamarin.Mac assembly for NSObject/etc subclasses
            var moduleIsXamarinMac = module.Name == "Xamarin.Mac.dll" || module.Name == "Microsoft.macOS.dll";

            var types = module.GetTypes();
            foreach (var typeDef in types)
            {
                CheckTypeAttributes(assemblyFullName, typeDef);

                if (!typeDef.IsClass)
                {
                    continue;
                }

                var foundINativeObjectImplementation = false;
                const string iNativeObjectInterfaceFullName = "ObjCRuntime.INativeObject";
                void CheckNativeObjectConstructors()
                {
                    // Looks for constructors that use IntPtr instead of NativeHandle. This will crash at runtime.
                    // See https://github.com/xamarin/xamarin-macios/blob/14d5620f5f8b6e5b7541695a22ef7376807c400e/dotnet/BreakingChanges.md#nsobjecthandle-and-inativeobjecthandle-changed-type-from-systemintptr-to-objcruntimenativehandle
                    if (typeDef.Methods.Any(m => m.IsConstructor && m.Parameters.Any(p => p.ParameterType.Name == "IntPtr")))
                    {
                        // TODO: Check that the ctor calls base? Is this possible?
                        diagnostics.Add($"In assembly '{assemblyFullName}': Type {typeDef.FullName} has a potentially dangerous IntPtr constructor");
                    }
                }

                if (typeDef.HasInterfaces)
                {
                    foreach (var interfaceImplementation in typeDef.Interfaces)
                    {
                        var interfaceTypeRef = interfaceImplementation.InterfaceType;
                        references.Add(interfaceTypeRef);

                        try
                        {
                            if (ReportIntPtrConstructors
                                && !foundINativeObjectImplementation
                                && !moduleIsXamarinMac
                                && interfaceTypeRef.FullName == iNativeObjectInterfaceFullName)
                            {
                                foundINativeObjectImplementation = true;
                                CheckNativeObjectConstructors();
                            }

                            var interfaceTypeDef = interfaceTypeRef.Resolve();
                            if (interfaceTypeDef != null)
                            {
                                if (interfaceTypeDef.HasMethods)
                                {
                                    foreach (var interfaceMethod in interfaceTypeDef.Methods)
                                    {
                                        if (interfaceMethod.HasGenericParameters || interfaceMethod.ContainsGenericParameter)
                                        {
                                            // it's non-trivial to match when generics are involved
                                            continue;
                                        }

                                        if (interfaceMethod.HasBody)
                                        {
                                            // Default method implementation provided by the interface itself
                                            continue;
                                        }

                                        bool sawGenerics = false;
                                        var matching = FindInterfaceMethodImplementation(typeDef, interfaceMethod, ref sawGenerics);
                                        if (matching == null && !sawGenerics)
                                        {
                                            var interfaceAssembly = GetAssemblyName(interfaceMethod);
                                            diagnostics.Add($"In assembly '{assemblyFullName}': Type {typeDef.FullName} does not implement interface method {interfaceMethod.FullName} from assembly {interfaceAssembly}");
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                // For some reason, typeDef.Interfaces does not always show an INativeObject implementation even
                // when it is there (via NSObject, typically). Resolve all base types to see if any of them
                // implement INativeObject.
                if (ReportIntPtrConstructors && !foundINativeObjectImplementation && !moduleIsXamarinMac)
                {
                    var candidateNativeTypeDef = typeDef;
                    while (candidateNativeTypeDef != null)
                    {
                        if (candidateNativeTypeDef.HasInterfaces && candidateNativeTypeDef.Interfaces.Any(i => i.InterfaceType.FullName == "ObjCRuntime.INativeObject"))
                        {
                            break;
                        }
                        candidateNativeTypeDef = ResolveBaseType(candidateNativeTypeDef);
                    }

                    if (candidateNativeTypeDef != null)
                    {
                        CheckNativeObjectConstructors();
                    }
                }

                if (typeDef.HasMethods)
                {
                    foreach (var methodDef in typeDef.Methods)
                    {
                        try
                        {
                            if (methodDef.HasOverrides)
                            {
                                foreach (var methodOverride in methodDef.Overrides)
                                {
                                    references.Add(methodOverride);
                                }
                            }

                            var baseMethod = GetBaseMethod(methodDef);
                            if (baseMethod != null)
                            {
                                var same = MetadataResolver.GetMethod(baseMethod.DeclaringType.Methods, methodDef);
                                if (same == null)
                                {
                                    diagnostics.Add($"In assembly '{assemblyFullName}': Failed to find base method for '{methodDef.FullName}'");
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        private void CheckTypeAttributes(string assemblyFullName, TypeDefinition typeDef)
        {
            if (!typeDef.HasCustomAttributes)
            {
                return;
            }

            var attributes = typeDef.CustomAttributes;
            bool hasCompilerGeneratedAttribute = false;
            bool hasTypeIdentifierAttribute = false;
            foreach (var attribute in attributes)
            {
                string fullName = attribute.AttributeType.FullName;
                if (fullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
                {
                    hasCompilerGeneratedAttribute = true;
                }
                else if (fullName == "System.Runtime.InteropServices.TypeIdentifierAttribute")
                {
                    hasTypeIdentifierAttribute = true;
                }
            }

            if (ReportEmbeddedInteropTypes && hasCompilerGeneratedAttribute && hasTypeIdentifierAttribute)
            {
                diagnostics.Add($"In assembly '{assemblyFullName}': Embedded interop type {typeDef.FullName}");
            }
        }

        private void CheckMembers(AssemblyDefinition assembly)
        {
            string assemblyFullName = assembly.Name.FullName;
            var module = assembly.MainModule;
            var references = module.GetTypeReferences().Concat(module.GetMemberReferences()).ToList();

            try
            {
                CheckTypeDefinitions(assemblyFullName, module, references);
            }
            catch
            {
            }

            foreach (var memberReference in references)
            {
                try
                {
                    var declaringType = memberReference.DeclaringType ?? (memberReference as TypeReference);
                    if (declaringType != null && declaringType.IsArray)
                    {
                        continue;
                    }

                    IMetadataScope scope = declaringType?.Scope;
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
                        string typeOrMember = memberReference is TypeReference ? "type" : "member";
                        diagnostics.Add($"In assembly '{assemblyFullName}': Failed to resolve {typeOrMember} reference '{memberReference.FullName}' in assembly '{referenceToAssembly}'");
                    }
                    else
                    {
                        var ivtUsage = TryGetIVTUsage(memberReference, resolved);
                        if (ivtUsage != null)
                        {
                            AddIVTUsage(ivtUsage);
                        }
                    }
                }
                catch (AssemblyResolutionException resolutionException)
                {
                    string unresolvedAssemblyName = resolutionException.AssemblyReference?.Name;
                    if (unresolvedAssemblyName == null || unresolvedAssemblies.Add(unresolvedAssemblyName))
                    {
                        diagnostics.Add($"In assembly '{assemblyFullName}': {resolutionException.Message}");
                    }
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"In assembly '{assemblyFullName}': {ex.Message}");
                }
            }
        }

        public static string GetAssemblyName(MemberReference memberReference)
        {
            var declaringType = memberReference.DeclaringType ?? (memberReference as TypeReference);
            if (declaringType == null)
            {
                return null;
            }

            IMetadataScope scope = declaringType.Scope;
            string referenceToAssembly = scope?.Name;

            if (scope is AssemblyNameReference assemblyNameReference)
            {
                referenceToAssembly = assemblyNameReference.FullName;
            }

            return referenceToAssembly;
        }

        private IVTUsage TryGetIVTUsage(MemberReference memberReference, IMemberDefinition definition)
        {
            string consumingModule = memberReference.Module.FileName;

            if (definition is MemberReference memberDefinition)
            {
                string definitionModule = memberDefinition.Module.FileName;

                if (consumingModule == definitionModule)
                {
                    return null;
                }

                if (AllPublic(memberDefinition))
                {
                    return null;
                }

                return new IVTUsage
                {
                    ExposingAssembly = definitionModule,
                    ConsumingAssembly = consumingModule,
                    Member = definition.ToString()
                };
            }

            return null;
        }

        private bool AllPublic(MemberReference memberReference)
        {
            return memberReference switch
            {
                TypeDefinition typeDefinition => AllPublic(typeDefinition),
                MethodDefinition methodDefinition => AllPublic(methodDefinition),
                FieldDefinition fieldDefinition => AllPublic(fieldDefinition),
                _ => true
            };
        }

        private static bool AllPublic(FieldDefinition field)
        {
            if (field.IsAssembly || field.IsFamilyAndAssembly)
            {
                return false;
            }

            var type = field.DeclaringType;
            return AllPublic(type);
        }

        private static bool AllPublic(MethodDefinition method)
        {
            if (method.IsAssembly || method.IsFamilyAndAssembly)
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
            var typeReferences = referencing.MainModule.GetTypeReferences();
            var types = GetTypes(reference);

            foreach (var referencedType in typeReferences)
            {
                if (referencedType.Scope == null ||
                    referencedType.Scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference ||
                    !string.Equals(referencedType.Scope.Name, reference.Name.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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
                        AddIVTUsage(ivtUsage);
                    }
                }
                else
                {
                    diagnostics.Add($"In assembly '{referencing.Name.FullName}': Failed to resolve type reference '{referencedType.FullName}' in assembly '{reference.Name}'");
                }
            }
        }

        private void AddIVTUsage(IVTUsage ivtUsage)
        {
            ivtUsages.Add(ivtUsage);
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
                types.Add(nested.FullName, nested.IsNestedPublic);
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

            if (!CallAssemblyLoadToResolveAssemblies)
            {
                return null;
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
