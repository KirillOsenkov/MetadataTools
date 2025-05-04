using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BinaryCompatChecker;

public partial class Checker
{
    private void ReportResults(CheckResult result, string baselineFile, string reportFile)
    {
        List<string> reportLines = new();

        foreach (var diagnostic in diagnostics.OrderBy(s => s))
        {
            var text = diagnostic.Replace('\r', ' ').Replace('\n', ' ');
            text = text.Replace(", Culture=neutral", "");
            reportLines.Add(text);
        }

        if (File.Exists(baselineFile))
        {
            var baseline = File.ReadLines(baselineFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
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
    }

    public static void WriteError(string text)
    {
        WriteLine(text, ConsoleColor.Red, Console.Error);
    }

    public static void WriteWarning(string text)
    {
        WriteLine(text, ConsoleColor.Yellow, Console.Error);
    }

    public static void WriteLine(string text = "", ConsoleColor? color = null, TextWriter writer = null)
    {
        writer ??= Console.Out;

        lock (writer)
        {
            Write(text, color, writer);
            writer.WriteLine();
        }
    }

    public static void Write(string text, ConsoleColor? color = null, TextWriter writer = null)
    {
        writer ??= Console.Out;

        lock (writer)
        {
            ConsoleColor originalColor = ConsoleColor.Gray;
            if (color != null)
            {
                originalColor = Console.ForegroundColor;
                Console.ForegroundColor = color.Value;
            }

            writer.Write(text);

            if (color != null)
            {
                Console.ForegroundColor = originalColor;
            }
        }
    }

    public static void OutputDiff(CommandLine commandLine, IEnumerable<string> baseline, IEnumerable<string> reportLines)
    {
        var removed = baseline.Except(reportLines, StringComparer.OrdinalIgnoreCase);
        var added = reportLines.Except(baseline, StringComparer.OrdinalIgnoreCase);

        if (removed.Any())
        {
            if (commandLine.EnableDefaultOutput || commandLine.OutputExpectedWarnings)
            {
                if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                {
                    WriteWarning("=================================");
                    WriteWarning("These expected lines are missing:");
                    WriteExpectedWarnings(removed);
                    WriteWarning("=================================");
                }
                else
                {
                    WriteExpectedWarnings(removed);
                }
            }
        }

        if (added.Any())
        {
            if (commandLine.EnableDefaultOutput || commandLine.OutputNewWarnings)
            {
                if (commandLine.EnableDefaultOutput || commandLine.OutputSummary)
                {
                    WriteError("=================================");
                    WriteError("These actual lines are new:");
                    WriteNewWarnings(added);
                    WriteError("=================================");
                }
                else
                {
                    WriteNewWarnings(added);
                }
            }
        }
    }

    private static void WriteExpectedWarnings(IEnumerable<string> removed)
    {
        foreach (var removedLine in removed)
        {
            WriteWarning(removedLine);
        }
    }

    private static void WriteNewWarnings(IEnumerable<string> added)
    {
        foreach (var addedLine in added)
        {
            WriteError(addedLine);
        }
    }

    private void ReportVersionMismatches(IReadOnlyList<AppConfigFile> appConfigFiles, Dictionary<string, List<VersionMismatch>> versionMismatchesByName)
    {
        int appConfigCount = appConfigFiles.Count;
        var allAppConfigNames = appConfigFiles.Select(f => f.FileName).ToArray();

        foreach (var versionMismatch in versionMismatchesByName.Values.SelectMany(list => list))
        {
            if (!versionMismatch.ExpectedReference.HasPublicKey && !versionMismatch.ActualAssembly.Name.HasPublicKey)
            {
                continue;
            }

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

            if (Framework.IsFrameworkRedirect(versionMismatch.ExpectedReference.Name))
            {
                continue;
            }

            if (resolvedFromFramework.Contains(actualFilePath))
            {
                continue;
            }

            actualFilePath = GetRelativePath(actualFilePath);

            string appConfigs = "";

            if (appConfigCount > 0)
            {
                var exceptConfigs =
                    versionMismatch.HandledByAppConfigs.Concat(
                    commandLine.IgnoreVersionMismatchForAppConfigs);
                var defendants = allAppConfigNames
                    .Except(exceptConfigs, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                // all app.configs have been exonerated, do not report the version mismatch
                if (defendants.Length == 0)
                {
                    continue;
                }
                else if (versionMismatch.HandledByAppConfigs.Count > 0)
                {
                    appConfigs = $". Not handled by: {string.Join(", ", defendants)}";
                }
            }

            diagnostics.Add($"Assembly `{versionMismatch.Referencer.Name.Name}` is referencing `{referencedFullName}` but found `{versionMismatch.ActualAssembly.FullName}` at `{actualFilePath}`{appConfigs}");
        }
    }

    private void WriteIVTReport(string primaryReportFile, string fileName = ".ivt.txt", Func<IVTUsage, bool> usageFilter = null)
    {
        string filePath = Path.ChangeExtension(primaryReportFile, fileName);
        var sb = new StringBuilder();

        var usages = ivtUsages
            .Where(u => !Framework.IsNetFrameworkAssembly(u.ConsumingAssembly) && !Framework.IsNetFrameworkAssembly(u.ExposingAssembly));

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
            if (commandLine.EnableDefaultOutput && !commandLine.IsBatchMode)
            {
                WriteLine($"Wrote {filePath}", ConsoleColor.Green);
            }
        }
    }

    private void ListExaminedAssemblies(string reportFile)
    {
        if (!commandLine.ListAssemblies)
        {
            return;
        }

        string filePath = Path.ChangeExtension(reportFile, ".Assemblies.txt");
        assembliesExamined.Sort();
        File.WriteAllLines(filePath, assembliesExamined);
        if (commandLine.EnableDefaultOutput && !commandLine.IsBatchMode)
        {
            WriteLine($"Wrote {filePath}", ConsoleColor.Green);
        }
    }
}