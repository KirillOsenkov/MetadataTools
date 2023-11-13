using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public partial class Checker
    {
        private string rootDirectory;
        IEnumerable<string> files;

        Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, AssemblyDefinition> resolveCache = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<AssemblyDefinition, Dictionary<string, bool>> assemblyToTypeList = new();

        List<string> assembliesExamined = new();
        List<string> reportLines = new();
        List<IVTUsage> ivtUsages = new();
        HashSet<string> unresolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> diagnostics = new(StringComparer.OrdinalIgnoreCase);

        public static bool ReportEmbeddedInteropTypes { get; set; }
        public static bool ReportIVT { get; set; }
        public static bool ReportVersionMismatch { get; set; } = true;
        public static bool ReportIntPtrConstructors { get; set; }

        [STAThread]
        static int Main(string[] args)
        {
            // Parse parameterized args
            var arguments = new List<string>(args);
            foreach (var arg in arguments.ToArray())
            {
                if (arg.Equals("/ignoreVersionMismatch", StringComparison.OrdinalIgnoreCase))
                {
                    ReportVersionMismatch = false;
                    arguments.Remove(arg);
                }

                if (arg.Equals("/embeddedInteropTypes", StringComparison.OrdinalIgnoreCase))
                {
                    ReportEmbeddedInteropTypes = true;
                    arguments.Remove(arg);
                }

                if (arg.Equals("/ivt", StringComparison.OrdinalIgnoreCase))
                {
                    ReportIVT = true;
                    arguments.Remove(arg);
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

            root = Path.GetFullPath(root);
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

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: BinaryCompatChecker [options] <root-folder> <output-report-file> [<config-file>]
    <root-folder|root-file>: root directory or root file where to start searching for files
    <output-report-file>: where to write the output report
    <config-file>: (optional) a file with include/exclude patterns
    Options:
        /ignoreNetFx: Ignores mismatches from framework assemblies");
        }

        private static bool IsWindows()
        {
            return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
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
                    !file.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".dll.config", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
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

        public Checker()
        {
            resolver = new CustomAssemblyResolver(this);
        }

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

            HashSet<string> frameworkAssemblyNames = GetFrameworkAssemblyNames();
            HashSet<string> assemblyNamesToIgnore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            assemblyNamesToIgnore.UnionWith(frameworkAssemblyNames);

            while (fileQueue.Count != 0)
            {
                string file = fileQueue.Dequeue();
                Console.WriteLine(file);

                var assemblyDefinition = Load(file);
                if (assemblyDefinition == null)
                {
                    continue;
                }

                if (IsNetFrameworkAssembly(assemblyDefinition))
                {
                    if (IsFacadeAssembly(assemblyDefinition))
                    {
                        var relativePath = GetRelativePath(file);
                        Log($"Facade assembly: {relativePath}");
                    }

                    continue;
                }

                // var relativePath = file.Substring(rootDirectory.Length + 1);
                // Log($"Assembly: {relativePath}: {assemblyDefinition.FullName}");

                var references = assemblyDefinition.MainModule.AssemblyReferences;
                foreach (var reference in references)
                {
                    if (assemblyNamesToIgnore.Contains(reference.Name))
                    {
                        continue;
                    }

                    var resolvedAssemblyDefinition = Resolve(reference);
                    if (resolvedAssemblyDefinition == null)
                    {
                        unresolvedAssemblies.Add(reference.Name);
                        diagnostics.Add($"In assembly '{assemblyDefinition.Name.FullName}': Failed to resolve assembly reference to '{reference.FullName}'");

                        continue;
                    }

                    if (IsNetFrameworkAssembly(resolvedAssemblyDefinition))
                    {
                        continue;
                    }

                    var resolvedPath = resolvedAssemblyDefinition.MainModule.FileName;
                    if (resolvedPath != null && visitedFiles.Add(resolvedPath))
                    {
                        fileQueue.Enqueue(resolvedPath);
                    }

                    CheckAssemblyReference(assemblyDefinition, resolvedAssemblyDefinition, reference);
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

            if (ReportIVT)
            {
                WriteIVTReport(reportFile);

                WriteIVTReport(
                    reportFile,
                    ".ivt.roslyn.txt",
                    u => IsRoslynAssembly(u.ExposingAssembly) && !IsRoslynAssembly(u.ConsumingAssembly));
            }

            return success;
        }

        public void CheckAssemblyReference(
            AssemblyDefinition referencing,
            AssemblyDefinition referenced,
            AssemblyNameReference reference)
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

            CheckTypes(referencing, referenced);
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
                    bool report = true;

                    TypeReference typeReference = memberReference as TypeReference ??
                        memberReference.DeclaringType;

                    if (typeReference != null && typeReference.Scope.Name is string scope && IsFrameworkName(scope))
                    {
                        report = false;
                    }

                    if (report)
                    {
                        diagnostics.Add($"In assembly '{assemblyFullName}': {ex.Message}");
                    }
                }
            }
        }
    }
}
