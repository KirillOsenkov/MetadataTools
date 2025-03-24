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
        private readonly AssemblyCache assemblyCache;
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
            this.assemblyCache = AssemblyCache.Instance;
            this.privateAssemblyCache = new AssemblyCache();
            resolver = new CustomAssemblyResolver(this);
        }

        public CheckResult Check()
        {
            var result = new CheckResult();
            result.Success = true;
            result.CommandLine = commandLine;

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
                    WriteError($"Baseline file doesn't exist: {baselineFile}");
                    result.Success = false;
                    return result;
                }
            }

            result.BaselineFile = baselineFile;
            result.ReportFile = reportFile;

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

            filesToVisit.UnionWith(fileQueue);

            if (commandLine.CheckPerAppConfig && appConfigFilePaths.Count > 1)
            {
                Checker[] allResults = Task.WhenAll(
                    appConfigFilePaths.Select(appConfigFilePath =>
                    {
                        Queue<string> filesForSubCheck = new Queue<string>(fileQueue);
                        var task = Task.Run(() =>
                        {
                            var subChecker = new Checker(commandLine);
                            subChecker.Check(filesForSubCheck, [appConfigFilePath]);
                            return subChecker;
                        });
                        return task;
                    })).Result;

                assembliesExamined.AddRange(allResults.SelectMany(r => r.assembliesExamined).Distinct());

                foreach (var d in allResults.SelectMany(r => r.diagnostics))
                {
                    bool[] reportedDiagnostics = new bool[allResults.Length];
                    for (int i = 0; i < allResults.Length; i++)
                    {
                        if (allResults[i].diagnostics.Contains(d))
                        {
                            reportedDiagnostics[i] = true;
                        }
                    }
                    if (reportedDiagnostics.All(x => x))
                    {
                        diagnostics.Add(d);
                    }
                    else
                    {
                        var diagnostic = $"{d}. Not handled by: {string.Join(", ", appConfigFilePaths
                            .Where((_, idx) => reportedDiagnostics[idx])
                            .Select(GetRelativePath))}";
                        diagnostics.Add(diagnostic);
                    }
                }

                ivtUsages.AddRange(
                    allResults.SelectMany(r => r.ivtUsages)
                    .DistinctBy(ivt => (ivt.ExposingAssembly, ivt.ConsumingAssembly, ivt.Member)));
            }
            else
            {
                Check(fileQueue, appConfigFilePaths);
            }

            List<string> reportLines = new();

            foreach (var diagnostic in diagnostics.OrderBy(s => s))
            {
                var text = diagnostic.Replace('\r', ' ').Replace('\n', ' ');
                text = text.Replace(", Culture=neutral", "");
                reportLines.Add(text);
            }

            if (File.Exists(baselineFile))
            {
                var baseline = File.ReadAllLines(baselineFile);
                if (!Enumerable.SequenceEqual(baseline, reportLines, StringComparer.OrdinalIgnoreCase))
                {
                    if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                    {
                        if (!commandLine.IsBatchMode)
                        {
                            string customPrompt = commandLine.CustomFailurePrompt != null ? $"{Environment.NewLine}{commandLine.CustomFailurePrompt}" : "";
                            WriteError($@"Binary compatibility check failed.{customPrompt}
The current report is different from the baseline file.
Baseline file: {baselineFile}
Report file: {reportFile}");
                        }
                    }

                    if (commandLine.IsBatchMode)
                    {
                        result.BaselineDiagnostics = baseline;
                        result.ActualDiagnostics = reportLines;
                    }
                    else
                    {
                        OutputDiff(commandLine, baseline, reportLines);
                    }

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
                        if (!commandLine.IsBatchMode)
                        {
                            WriteLine($"Binary compatibility report matches the baseline file.", ConsoleColor.Green);
                        }
                    }
                }
            }
            else if (!File.Exists(reportFile))
            {
                if (reportLines.Count > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(reportFile));

                    // initial baseline creation mode
                    File.WriteAllLines(reportFile, reportLines);

                    if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                    {
                        if (!commandLine.IsBatchMode)
                        {
                            WriteError("Binary compatibility check failed.");
                            if (!string.IsNullOrEmpty(commandLine.CustomFailurePrompt))
                            {
                                WriteError(commandLine.CustomFailurePrompt);
                            }

                            WriteError($"Wrote {reportFile}");
                        }
                    }

                    result.BaselineDiagnostics = Array.Empty<string>();
                    result.ActualDiagnostics = reportLines;

                    if (!commandLine.IsBatchMode)
                    {
                        OutputDiff(commandLine, Array.Empty<string>(), reportLines);
                    }

                    result.Success = false;
                }
            }

            ListExaminedAssemblies(reportFile);

            if (commandLine.ReportIVT)
            {
                WriteIVTReport(reportFile);

                WriteIVTReport(
                    reportFile,
                    ".ivt.roslyn.txt",
                    u => Framework.IsRoslynAssembly(u.ExposingAssembly) && !Framework.IsRoslynAssembly(u.ConsumingAssembly));
            }

            privateAssemblyCache?.Clear();
            ((CustomAssemblyResolver)resolver).Clear();

            return result;
        }

        private void Check(Queue<string> fileQueue, IEnumerable<string> appConfigFilePaths)
        {
            foreach (var appConfigFilePath in appConfigFilePaths)
            {
                bool ignoreVersionMismatch = commandLine.IgnoreVersionMismatchForAppConfigs.Contains(Path.GetFileName(appConfigFilePath), StringComparer.OrdinalIgnoreCase);

                if (commandLine.EnableDefaultOutput && !commandLine.IsBatchMode)
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
        public IReadOnlyList<string> BaselineDiagnostics { get; set; }
        public IReadOnlyList<string> ActualDiagnostics { get; set; }
        public string BaselineFile { get; internal set; }
        public string ReportFile { get; internal set; }
        public CommandLine CommandLine { get; internal set; }
    }
}
