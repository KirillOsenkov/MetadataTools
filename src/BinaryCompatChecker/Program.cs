using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public partial class Checker
    {
        private readonly Dictionary<string, AssemblyDefinition> resolveCache = new(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> assembliesExamined = new();
        private readonly HashSet<AssemblyDefinition> assemblyDefinitionsExamined = new();
        private readonly List<AppConfigFile> appConfigFiles = new();
        private readonly List<string> reportLines = new();
        private readonly List<IVTUsage> ivtUsages = new();
        private readonly HashSet<string> unresolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> diagnostics = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> visitedFiles = new(CommandLine.PathComparer);
        private readonly AssemblyCache assemblyCache;

        private CommandLine commandLine;

        [STAThread]
        static int Main(string[] args)
        {
            var commandLine = CommandLine.Parse(args);
            if (commandLine == null)
            {
                return -1;
            }

            if (commandLine.ConfigFile != null)
            {
                var configuration = Configuration.Read(commandLine.ConfigFile);
                var tasks = new List<Task<CheckResult>>();
                foreach (var invocation in configuration.FoldersToCheck)
                {
                    var line = invocation.GetCommandLine(commandLine);
                    line.IsBatchMode = true;
                    var task = Task.Run(() =>
                    {
                        var result = new Checker(line).Check();
                        return result;
                    });
                    task.Wait();
                    tasks.Add(task);
                }

                Task.WaitAll(tasks.ToArray());
                bool success = tasks.All(t => t.Result.Success);
                return success ? 0 : 1;
            }

            if (commandLine.ReplicateBindingRedirects)
            {
                AppConfigFile.ReplicateBindingRedirects(commandLine.SourceAppConfig, commandLine.DestinationAppConfigs);
                return 0;
            }

            var result = new Checker(commandLine).Check();
            return result.Success ? 0 : 1;
        }

        public static bool IsWindows { get; } = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        public Checker(CommandLine commandLine)
        {
            this.commandLine = commandLine;
            this.assemblyCache = AssemblyCache.Instance;
            resolver = new CustomAssemblyResolver(this);
        }

        public CheckResult Check()
        {
            var result = new CheckResult();
            result.Success = true;

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
                    result.Success = false;
                    return result;
                }
            }

            var appConfigFilePaths = new List<string>();

            if (commandLine.ClosureOnlyMode)
            {
                foreach (var closureRoot in commandLine.ClosureRootFiles)
                {
                    if (!closureRoot.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string candidateConfig = closureRoot + ".config";
                    if (File.Exists(candidateConfig))
                    {
                        appConfigFilePaths.Add(candidateConfig);
                    }
                }
            }

            Queue<string> fileQueue = new(commandLine.ClosureRootFiles);
            foreach (var file in commandLine.Files)
            {
                if (file.EndsWith(".config", CommandLine.PathComparison))
                {
                    if (file.EndsWith(".exe.config", CommandLine.PathComparison) ||
                        file.EndsWith(".dll.config", CommandLine.PathComparison) ||
                        string.Equals(Path.GetFileName(file), "web.config", CommandLine.PathComparison) ||
                        string.Equals(Path.GetFileName(file), "app.config", CommandLine.PathComparison))
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

                if (commandLine.EnableDefaultOutput)
                {
                    Write(appConfigFilePath, ConsoleColor.Magenta);
                    if (ignoreVersionMismatch)
                    {
                        Write(" - ignoring version mismatches", ConsoleColor.DarkMagenta);
                    }

                    WriteLine();
                }

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
                if (!visitedFiles.Add(file))
                {
                    continue;
                }

                var assemblyDefinition = Load(file);
                if (assemblyDefinition == null)
                {
                    continue;
                }

                string targetFramework = GetTargetFramework(assemblyDefinition);

                if (commandLine.EnableDefaultOutput)
                {
                    Write(file);
                    Write($" {assemblyDefinition.Name.Version}", color: ConsoleColor.DarkCyan);
                    if (targetFramework != null)
                    {
                        Write($" {targetFramework}", color: ConsoleColor.DarkGreen);
                    }

                    WriteLine("");
                }

                if (Framework.IsNetFrameworkAssembly(assemblyDefinition))
                {
                    if (Framework.IsFacadeAssembly(assemblyDefinition) && commandLine.ReportFacade)
                    {
                        var relativePath = GetRelativePath(file);
                        diagnostics.Add($"Facade assembly: {relativePath}");
                    }

                    if (!commandLine.AnalyzeFrameworkAssemblies)
                    {
                        continue;
                    }
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

                    if (Framework.IsNetFrameworkAssembly(resolvedAssemblyDefinition))
                    {
                        continue;
                    }

                    if (commandLine.ClosureOnlyMode)
                    {
                        fileQueue.Enqueue(referenceFilePath);
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
                    if (!Enumerable.SequenceEqual(baseline, reportLines, StringComparer.OrdinalIgnoreCase))
                    {
                        if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                        {
                            WriteError($@"Binary compatibility check failed.
 The current assembly binary compatibility report is different from the baseline file.
 Baseline file: {baselineFile}
 Wrote report file: {reportFile}");
                        }

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

                        result.Success = false;
                    }
                    else
                    {
                        if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                        {
                            WriteLine($"Binary compatibility report matches the baseline file.", ConsoleColor.Green);
                        }
                    }
                }
                else if (!File.Exists(reportFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(reportFile));

                    // initial baseline creation mode
                    File.WriteAllLines(reportFile, reportLines);

                    if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                    {
                        WriteError("Binary compatibility check failed.");
                        WriteError($"Wrote {reportFile}");
                    }

                    OutputDiff(Array.Empty<string>(), reportLines);
                    result.Success = false;
                }

                ListExaminedAssemblies(reportFile);
            }
            else
            {
                if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                {
                    WriteLine("No issues found", ConsoleColor.Green);
                }
            }

            if (commandLine.ReportIVT)
            {
                WriteIVTReport(reportFile);

                WriteIVTReport(
                    reportFile,
                    ".ivt.roslyn.txt",
                    u => Framework.IsRoslynAssembly(u.ExposingAssembly) && !Framework.IsRoslynAssembly(u.ConsumingAssembly));
            }

            return result;
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
    }

    public class CheckResult
    {
        public bool Success { get; set; }
    }
}
