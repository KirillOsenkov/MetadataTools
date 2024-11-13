using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public partial class Checker
    {
        private readonly Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssemblyDefinition> resolveCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AssemblyDefinition, Dictionary<string, bool>> assemblyToTypeList = new();

        private readonly List<string> assembliesExamined = new();
        private readonly List<AppConfigFile> appConfigFiles = new();
        private readonly List<string> reportLines = new();
        private readonly List<IVTUsage> ivtUsages = new();
        private readonly HashSet<string> unresolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> diagnostics = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> files;

        private static CommandLine commandLine;

        [STAThread]
        static int Main(string[] args)
        {
            commandLine = CommandLine.Parse(args);
            if (commandLine == null)
            {
                return -1;
            }

            if (commandLine.ReplicateBindingRedirects)
            {
                AppConfigFile.ReplicateBindingRedirects(commandLine.SourceAppConfig, commandLine.DestinationAppConfigs);
                return 0;
            }

            bool success = new Checker().Check();
            return success ? 0 : 1;
        }

        public static bool IsWindows { get; } = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        public Checker()
        {
            resolver = new CustomAssemblyResolver(this);
            files = new(commandLine.Files, CommandLine.PathComparer);
        }

        /// <returns>true if the check succeeded, false if the report is different from the baseline</returns>
        public bool Check()
        {
            bool success = true;

            string reportFile = commandLine.ReportFile;
            reportFile = Path.GetFullPath(reportFile);

            string baselineFile = commandLine.BaselineFile;
            if (string.IsNullOrWhiteSpace(baselineFile))
            {
                baselineFile = reportFile;
            }
            else
            {
                baselineFile = Path.GetFullPath(baselineFile);
                if (!File.Exists(baselineFile))
                {
                    WriteError($"Baseline file doesn't exist: {commandLine.BaselineFile}");
                    return false;
                }
            }

            var appConfigFilePaths = new List<string>();

            Queue<string> fileQueue = new(commandLine.ClosureRootFiles);
            foreach (var file in commandLine.Files)
            {
                if (file.EndsWith(".config", CommandLine.PathComparison))
                {
                    if (file.EndsWith(".exe.config", CommandLine.PathComparison) ||
                        file.EndsWith(".dll.config", CommandLine.PathComparison) ||
                        string.Equals(Path.GetFileName(file), "web.config", CommandLine.PathComparison))
                    {
                        appConfigFilePaths.Add(file);
                        continue;
                    }
                }

                fileQueue.Enqueue(file);
            }

            foreach (var appConfigFilePath in appConfigFilePaths)
            {
                bool ignoreVersionMismatch = commandLine.IgnoreVersionMismatchForAppConfigs.Contains(Path.GetFileName(appConfigFilePath), StringComparer.OrdinalIgnoreCase);

                Write(appConfigFilePath, ConsoleColor.Magenta);
                if (ignoreVersionMismatch)
                {
                    Write(" - ignoring version mismatches", ConsoleColor.DarkMagenta);
                }

                WriteLine();

                var appConfigFileName = Path.GetFileName(appConfigFilePath);
                var appConfigFile = AppConfigFile.Read(appConfigFilePath);
                if (ignoreVersionMismatch)
                {
                    appConfigFile.IgnoreVersionMismatch = true;
                }

                if (appConfigFile.Errors.Any())
                {
                    foreach (var error in appConfigFile.Errors)
                    {
                        diagnostics.Add($"App.config: '{appConfigFileName}': {error}");
                    }
                }

                appConfigFiles.Add(appConfigFile);
            }

            Dictionary<string, IEnumerable<string>> referenceMap = new(CommandLine.PathComparer);

            while (fileQueue.Count != 0)
            {
                string file = fileQueue.Dequeue();

                var assemblyDefinition = Load(file);
                if (assemblyDefinition == null)
                {
                    continue;
                }

                string targetFramework = GetTargetFramework(assemblyDefinition);

                Write(file);
                Write($" {assemblyDefinition.Name.Version}", color: ConsoleColor.DarkCyan);
                if (targetFramework != null)
                {
                    Write($" {targetFramework}", color: ConsoleColor.DarkGreen);
                }

                WriteLine("");

                if (IsNetFrameworkAssembly(assemblyDefinition))
                {
                    if (IsFacadeAssembly(assemblyDefinition) && commandLine.ReportFacade)
                    {
                        var relativePath = GetRelativePath(file);
                        diagnostics.Add($"Facade assembly: {relativePath}");
                    }

                    continue;
                }

                // var relativePath = file.Substring(rootDirectory.Length + 1);
                // Log($"Assembly: {relativePath}: {assemblyDefinition.FullName}");

                var references = assemblyDefinition.MainModule.AssemblyReferences;
                List<string> referencePaths = new();
                foreach (var reference in references)
                {
                    currentResolveDirectory = Path.GetDirectoryName(file);
                    var resolvedAssemblyDefinition = Resolve(reference);
                    if (resolvedAssemblyDefinition == null)
                    {
                        unresolvedAssemblies.Add(reference.Name);
                        if (commandLine.ReportMissingAssemblies)
                        {
                            diagnostics.Add($"In assembly '{assemblyDefinition.Name.FullName}': Failed to resolve assembly reference to '{reference.FullName}'");
                        }

                        continue;
                    }

                    string referenceFilePath = resolvedAssemblyDefinition.MainModule.FileName;
                    referencePaths.Add(referenceFilePath);

                    CheckAssemblyReferenceVersion(assemblyDefinition, resolvedAssemblyDefinition, reference);

                    if (IsNetFrameworkAssembly(resolvedAssemblyDefinition))
                    {
                        continue;
                    }

                    CheckTypes(assemblyDefinition, resolvedAssemblyDefinition);
                }

                referenceMap[file] = referencePaths;

                CheckMembers(assemblyDefinition);
            }

            CheckAppConfigFiles(appConfigFiles);

            if (commandLine.ReportUnreferencedAssemblies)
            {
                HashSet<string> closure = new(CommandLine.PathComparer);
                BuildClosure(commandLine.ClosureRootFiles);

                void BuildClosure(IEnumerable<string> assemblies)
                {
                    foreach (var assembly in assemblies)
                    {
                        if (closure.Add(assembly) && referenceMap.TryGetValue(assembly, out var references))
                        {
                            BuildClosure(references);
                        }
                    }
                }

                foreach (var file in commandLine.Files)
                {
                    if (file.EndsWith(".config", CommandLine.PathComparison))
                    {
                        continue;
                    }

                    if (!closure.Contains(file))
                    {
                        diagnostics.Add("Unreferenced assembly: " + GetRelativePath(file));
                    }
                }
            }

            foreach (var ex in diagnostics.OrderBy(s => s))
            {
                Log(ex);
            }

            if (reportLines.Count > 0)
            {
                if (File.Exists(baselineFile))
                {
                    var baseline = File.ReadAllLines(baselineFile);
                    if (!Enumerable.SequenceEqual(baseline, reportLines))
                    {
                        WriteError($@"Binary compatibility check failed.
 The current assembly binary compatibility report is different from the baseline file.
 Baseline file: {baselineFile}
 Wrote report file: {reportFile}");
                        OutputDiff(baseline, reportLines);
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(reportFile));
                            File.WriteAllLines(reportFile, reportLines);
                        }
                        catch (Exception ex)
                        {
                            WriteError(ex.Message);
                        }

                        success = false;
                    }
                    else
                    {
                        WriteLine($"Binary compatibility report matches the baseline file.", ConsoleColor.Green);
                    }
                }
                else if (!File.Exists(reportFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(reportFile));

                    // initial baseline creation mode
                    File.WriteAllLines(reportFile, reportLines);
                    WriteLine($"Wrote {reportFile}", ConsoleColor.Green);
                }

                ListExaminedAssemblies(reportFile);
            }
            else
            {
                WriteLine("No issues found", ConsoleColor.Green);
            }

            if (commandLine.ReportIVT)
            {
                WriteIVTReport(reportFile);

                WriteIVTReport(
                    reportFile,
                    ".ivt.roslyn.txt",
                    u => IsRoslynAssembly(u.ExposingAssembly) && !IsRoslynAssembly(u.ConsumingAssembly));
            }

            return success;
        }

        public void CheckAssemblyReferenceVersion(
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

            HashSet<(AssemblyDefinition assemblyDefinition, string referenceName)> assembliesWithFailedMemberRefs = new();

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
                        bool report = memberReference is TypeReference ? commandLine.ReportMissingTypes : commandLine.ReportMissingMembers;
                        if (report)
                        {
                            string typeOrMember = memberReference is TypeReference ? "type" : "member";
                            diagnostics.Add($"In assembly '{assemblyFullName}': Failed to resolve {typeOrMember} reference '{memberReference.FullName}' in assembly '{referenceToAssembly}'");

                            var resolveKey = GetResolveKey(referenceToAssembly);
                            if (referenceToAssembly != null && resolveCache.TryGetValue(resolveKey, out var referencedAssemblyDefinition) && referencedAssemblyDefinition != null)
                            {
                                assembliesWithFailedMemberRefs.Add((referencedAssemblyDefinition, referenceToAssembly));
                            }
                        }
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

                    if (typeReference != null && typeReference.Scope?.Name is string scope && IsFrameworkName(scope))
                    {
                        report = false;
                    }

                    if (report)
                    {
                        diagnostics.Add($"In assembly '{assemblyFullName}': {ex.Message}");
                    }
                }
            }

            foreach (var assemblyWithFailedMemberRefs in assembliesWithFailedMemberRefs)
            {
                var assemblyDefinition = assemblyWithFailedMemberRefs.assemblyDefinition;
                string relativePath = GetRelativePath(assemblyDefinition.MainModule.FileName);
                diagnostics.Add($"In assembly '{assemblyFullName}': reference '{assemblyWithFailedMemberRefs.referenceName}' resolved from '{relativePath}' as '{assemblyDefinition.FullName}'");
            }
        }
    }
}
