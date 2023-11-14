using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BinaryCompatChecker;

public partial class Checker
{
    public static void WriteError(string text)
    {
        WriteLine(text, ConsoleColor.Red, Console.Error);
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

    private void OutputDiff(IEnumerable<string> baseline, IEnumerable<string> reportLines)
    {
        var removed = baseline.Except(reportLines);
        var added = reportLines.Except(baseline);

        if (removed.Any())
        {
            WriteError("=================================");
            WriteError("These expected lines are missing:");
            foreach (var removedLine in removed)
            {
                WriteError(removedLine);
            }

            WriteError("=================================");
        }

        if (added.Any())
        {
            WriteError("=================================");
            WriteError("These actual lines are new:");
            foreach (var addedLine in added)
            {
                WriteError(addedLine);
            }

            WriteError("=================================");
        }
    }

    private void Log(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        text = text.Replace(", Culture=neutral", "");
        reportLines.Add(text);
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

            actualFilePath = GetRelativePath(actualFilePath);

            diagnostics.Add($"Assembly `{versionMismatch.Referencer.Name.Name}` is referencing `{referencedFullName}` but found `{versionMismatch.ActualAssembly.FullName}` at `{actualFilePath}`");
        }
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
            WriteLine($"Wrote {filePath}", ConsoleColor.Green);
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
        WriteLine($"Wrote {filePath}", ConsoleColor.Green);
    }
}