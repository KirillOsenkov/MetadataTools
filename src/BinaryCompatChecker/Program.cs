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
        private readonly List<IVTUsage> ivtUsages = new();
        private readonly HashSet<string> unresolvedAssemblies = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> diagnostics = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> visitedFiles = new(CommandLine.PathComparer);
        private readonly HashSet<string> filesToVisit = new(CommandLine.PathComparer);
        private readonly AssemblyCache privateAssemblyCache;

        private CommandLine commandLine;

        [STAThread]
        static int Main(string[] args)
        {
            var commandLine = CommandLine.Parse(args, Environment.CurrentDirectory);
            if (commandLine == null)
            {
                return -1;
            }

            if (commandLine.ConfigFile != null)
            {
                return RunInBatchMode(commandLine);
            }

            if (commandLine.ReplicateBindingRedirects)
            {
                AppConfigFile.ReplicateBindingRedirects(commandLine.SourceAppConfig, commandLine.DestinationAppConfigs);
                return 0;
            }

            var result = new Checker(commandLine).Check();
            return result.Success ? 0 : 1;
        }

        private static int RunInBatchMode(CommandLine commandLine)
        {
            Configuration configuration;

            try
            {
                configuration = Configuration.Read(commandLine.ConfigFile);
            }
            catch (Exception ex)
            {
                WriteError($"Invalid configuration file: {commandLine.ConfigFile}");
                WriteError(ex.Message);
                return 2;
            }

            if (configuration.CustomFailurePrompt != null)
            {
                commandLine.CustomFailurePrompt = configuration.CustomFailurePrompt;
            }

            var tasks = new List<Task<CheckResult>>();
            foreach (var invocation in configuration.FoldersToCheck)
            {
                var task = Task.Run(() =>
                {
                    var line = invocation.GetCommandLine(commandLine);
                    if (line == null)
                    {
                        return new CheckResult
                        {
                            Success = false
                        };
                    }

                    line.IsBatchMode = true;
                    var result = new Checker(line).Check();
                    return result;
                });
                tasks.Add(task);
            }

            bool success = true;
            bool hasDiff = false;

            foreach (var task in tasks)
            {
                var checkResult = task.Result;
                if (!checkResult.Success)
                {
                    success = false;
                    if (checkResult.BaselineDiagnostics != null || checkResult.ActualDiagnostics != null)
                    {
                        hasDiff = true;
                    }
                }
            }

            if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
            {
                if (success)
                {
                    WriteLine($"Binary compatibility check succeeded.", ConsoleColor.Green);
                }
                else
                {
                    string customPrompt = commandLine.CustomFailurePrompt != null ? $"{Environment.NewLine}{commandLine.CustomFailurePrompt}" : "";
                    WriteError($@"Binary compatibility check failed.{customPrompt}");
                }
            }

            if (hasDiff)
            {
                WriteError($@"
The following report files are different from the baseline file:");
                foreach (var task in tasks)
                {
                    var checkResult = task.Result;
                    if (!checkResult.Success)
                    {
                        if (checkResult.BaselineFile != null || checkResult.ReportFile != null)
                        {
                            WriteError($@"Baseline file: {checkResult.BaselineFile}
Report file: {checkResult.ReportFile}");
                        }

                        if (checkResult.BaselineDiagnostics != null || checkResult.ActualDiagnostics != null)
                        {
                            var baselineDiagnostics = checkResult.BaselineDiagnostics ?? Array.Empty<string>();
                            var actualDiagnostics = checkResult.ActualDiagnostics ?? Array.Empty<string>();
                            OutputDiff(
                                checkResult.CommandLine,
                                baselineDiagnostics,
                                actualDiagnostics);
                        }
                    }
                }
            }

            return success ? 0 : 1;
        }

        public static bool IsWindows { get; } = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

        public Checker(CommandLine commandLine)
        {
            this.commandLine = commandLine;
            this.privateAssemblyCache = new AssemblyCache();
            resolver = new CustomAssemblyResolver(this);
        }

        private void ComputeBaselineAndReportFile(CheckResult result)
        {
            string reportFile = commandLine.ReportFile;
            if (reportFile == null && commandLine.BaselineFile != null)
            {
                reportFile = commandLine.BaselineFile;
            }

            if (reportFile == null)
            {
                reportFile = "BinaryCompatReport.txt";
            }

            if (commandLine.ReportDirectory != null && !Path.IsPathRooted(reportFile))
            {
                reportFile = Path.Combine(commandLine.ReportDirectory, reportFile);
            }

            reportFile = Path.GetFullPath(reportFile);

            string baselineFile = commandLine.BaselineFile;
            if (string.IsNullOrWhiteSpace(baselineFile))
            {
                baselineFile = reportFile;
            }
            else
            {
                if (commandLine.BaselineDirectory != null && !Path.IsPathRooted(baselineFile))
                {
                    baselineFile = Path.Combine(commandLine.BaselineDirectory, baselineFile);
                }

                baselineFile = Path.GetFullPath(baselineFile);
                if (!File.Exists(baselineFile))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Baseline file doesn't exist: {baselineFile}";
                }
            }

            result.BaselineFile = baselineFile;
            result.ReportFile = reportFile;
        }

        public CheckResult Check()
        {
            var result = new CheckResult();
            result.Success = true;
            result.CommandLine = commandLine;

            ComputeBaselineAndReportFile(result);
            if (!result.Success)
            {
                WriteError(result.ErrorMessage);
                return result;
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
                if (AppConfigFile.IsAppConfigFile(file))
                {
                    appConfigFilePaths.Add(file);
                    continue;
                }

                fileQueue.Enqueue(file);
            }

            filesToVisit.UnionWith(fileQueue);

            foreach (var appConfigFilePath in appConfigFilePaths)
            {
                AddAppConfigFile(appConfigFilePath);
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

                if (commandLine.EnableDefaultOutput && !commandLine.IsBatchMode)
                {
                    Write(file);
                    Write($" {assemblyDefinition.Name.Version}", color: ConsoleColor.DarkCyan);
                    if (targetFramework != null)
                    {
                        Write($" {targetFramework}", color: ConsoleColor.DarkGreen);
                    }

                    WriteLine();
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

            ReportUnreferencedAssemblies(referenceMap);

            ReportResults(result);

            privateAssemblyCache?.Clear();
            ((CustomAssemblyResolver)resolver).Clear();

            return result;
        }

        private void AddAppConfigFile(string appConfigFilePath)
        {
            var appConfigFileName = Path.GetFileName(appConfigFilePath);
            bool ignoreVersionMismatch = commandLine.IgnoreVersionMismatchForAppConfigs.Contains(
                appConfigFileName, StringComparer.OrdinalIgnoreCase);

            if (commandLine.EnableDefaultOutput && !commandLine.IsBatchMode)
            {
                lock (Console.Out)
                {
                    Write(appConfigFilePath, ConsoleColor.Magenta);
                    if (ignoreVersionMismatch)
                    {
                        Write(" - ignoring version mismatches", ConsoleColor.DarkMagenta);
                    }

                    WriteLine();
                }
            }

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

        private void ReportUnreferencedAssemblies(Dictionary<string, IEnumerable<string>> referenceMap)
        {
            if (!commandLine.ReportUnreferencedAssemblies)
            {
                return;
            }

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
        public string ErrorMessage { get; set; }
        public IReadOnlyList<string> BaselineDiagnostics { get; set; }
        public IReadOnlyList<string> ActualDiagnostics { get; set; }
        public string BaselineFile { get; internal set; }
        public string ReportFile { get; internal set; }
        public CommandLine CommandLine { get; internal set; }
    }
}
