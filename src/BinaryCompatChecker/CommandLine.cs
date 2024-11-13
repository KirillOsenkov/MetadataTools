using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BinaryCompatChecker;

public class CommandLine
{
    public bool ReportEmbeddedInteropTypes { get; set; }
    public bool ReportIVT { get; set; }
    public bool ReportVersionMismatch { get; set; } = true;
    public bool ReportIntPtrConstructors { get; set; }
    public bool ReportUnreferencedAssemblies { get; set; } = false;
    public bool ReportFacade { get; set; } = true;
    public bool ReportMissingAssemblies { get; set; } = true;
    public bool ReportMissingTypes { get; set; } = true;
    public bool ReportMissingMembers { get; set; } = true;
    public bool ReportInterfaceMismatch { get; set; } = true;

    public bool OutputExpectedWarnings { get; set; }
    public bool OutputNewWarnings { get; set; }
    public bool OutputSummary { get; set; }
    public bool EnableDefaultOutput { get; set; } = true;

    public bool ReplicateBindingRedirects { get; set; }
    public string SourceAppConfig { get; set; }
    public IReadOnlyList<string> DestinationAppConfigs { get; set; }

    public IReadOnlyList<string> IgnoreVersionMismatchForAppConfigs { get; set; } = Array.Empty<string>();

    public string BaselineFile { get; set; }
    public string ReportFile { get; set; } = "BinaryCompatReport.txt";
    public bool ListAssemblies { get; set; }

    public bool Recursive { get; set; }

    public static readonly StringComparison PathComparison = Checker.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static readonly StringComparer PathComparer = Checker.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private HashSet<string> rootDirectories = new(PathComparer);
    public IEnumerable<string> RootDirectories => rootDirectories;

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        if (!result.Process(args))
        {
            return null;
        }

        return result;
    }

    public IEnumerable<string> Files => files;
    public IEnumerable<string> ClosureRootFiles => closureRootFiles;
    public IEnumerable<string> AllDirectories => allDirectories;

    public bool IsClosureRoot(string filePath)
    {
        if (filePath.EndsWith(".config", PathComparison))
        {
            return false;
        }

        foreach (var root in closureRootPatterns)
        {
            if (filePath.Contains(root, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    // Commonly used:
    // @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin",
    // @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\CommonExtensions\Microsoft\NuGet",
    public IList<string> CustomResolveDirectories { get; } = new List<string>();

    HashSet<string> inclusions = new(PathComparer);
    HashSet<string> exclusions = new(PathComparer);
    HashSet<string> files = new(PathComparer);
    HashSet<string> patterns = new();
    HashSet<string> closureRootPatterns = new(PathComparer);
    HashSet<string> allDirectories = new(PathComparer);
    List<string> closureRootFiles = new();
    IncludeExcludePattern includeExclude;

    public bool Process(string[] args)
    {
        // Parse parameterized args
        var arguments = new List<string>(args);

        var currentDirectory = Environment.CurrentDirectory;

        var responseFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (arguments.FirstOrDefault(a => a.StartsWith("@")) is string responseFile)
        {
            arguments.Remove(responseFile);
            responseFile = responseFile.Substring(1);
            var filePath = Path.GetFullPath(responseFile);
            if (File.Exists(filePath))
            {
                if (!responseFiles.Add(filePath))
                {
                    Checker.WriteError($"Response file can't be included more than once: {responseFile}");
                    return false;
                }

                var lines = File.ReadAllLines(filePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    {
                        continue;
                    }

                    arguments.Add(line.Trim());
                }
            }
            else
            {
                Checker.WriteError("Response file doesn't exist: " + responseFile);
                return false;
            }
        }

        var helpArgument = arguments.FirstOrDefault(a => a == "/?" || a == "-?" || a == "-h" || a == "/h" || a == "-help" || a == "/help");
        if (helpArgument != null)
        {
            PrintUsage();
            return false;
        }

        foreach (var arg in arguments.ToArray())
        {
            if (arg.StartsWith("/ignoreVersionMismatch", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("-ignoreVersionMismatch", StringComparison.OrdinalIgnoreCase))
            {
                ReportVersionMismatch = false;
                arguments.Remove(arg);

                int prefixLength = "/ignoreVersionMismatch".Length;
                if (arg.Length > prefixLength + 1 && arg[prefixLength] == ':')
                {
                    var configs = arg.Substring(prefixLength + 1).Split(';', ',');
                    if (configs.Length > 0)
                    {
                        IgnoreVersionMismatchForAppConfigs = configs;
                        ReportVersionMismatch = true;
                    }
                }

                continue;
            }

            if (arg.Equals("/ignoreFacade", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ignoreFacade", StringComparison.OrdinalIgnoreCase))
            {
                ReportFacade = false;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/ignoreMissingAssemblies", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ignoreMissingAssemblies", StringComparison.OrdinalIgnoreCase))
            {
                ReportMissingAssemblies = false;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/ignoreMissingTypes", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ignoreMissingTypes", StringComparison.OrdinalIgnoreCase))
            {
                ReportMissingTypes = false;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/ignoreMissingMembers", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ignoreMissingMembers", StringComparison.OrdinalIgnoreCase))
            {
                ReportMissingMembers = false;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/ignoreInterfaces", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ignoreInterfaces", StringComparison.OrdinalIgnoreCase))
            {
                ReportInterfaceMismatch = false;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/outputExpectedWarnings", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-outputExpectedWarnings", StringComparison.OrdinalIgnoreCase))
            {
                OutputExpectedWarnings = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/outputNewWarnings", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-outputNewWarnings", StringComparison.OrdinalIgnoreCase))
            {
                OutputNewWarnings = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/outputSummary", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-outputSummary", StringComparison.OrdinalIgnoreCase))
            {
                OutputSummary = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/embeddedInteropTypes", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-embeddedInteropTypes", StringComparison.OrdinalIgnoreCase))
            {
                ReportEmbeddedInteropTypes = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/ivt", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-ivt", StringComparison.OrdinalIgnoreCase))
            {
                ReportIVT = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/s", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
            {
                Recursive = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.Equals("/intPtrCtors", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-intPtrCtors", StringComparison.OrdinalIgnoreCase))
            {
                ReportIntPtrConstructors = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("/baseline:") || arg.StartsWith("-baseline:"))
            {
                var report = arg.Substring(10);
                report = report.Trim('"');
                arguments.Remove(arg);
                BaselineFile = report;
                continue;
            }

            if (arg.Equals("/replicatebindingredirects", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-replicatebindingredirects", StringComparison.OrdinalIgnoreCase))
            {
                arguments.Remove(arg);
                ReplicateBindingRedirects = true;
                continue;
            }

            if (arg.StartsWith("/out:") || arg.StartsWith("-out:"))
            {
                var report = arg.Substring(5);
                report = report.Trim('"');
                arguments.Remove(arg);
                ReportFile = report;
                continue;
            }

            if (arg.StartsWith("/l") || arg.StartsWith("-l"))
            {
                if (arg.Length == 2)
                {
                    ListAssemblies = true;
                }

                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("!") && arg.Length > 1)
            {
                string pattern = arg.Substring(1).Trim('"');
                exclusions.Add(pattern);
                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("/closure:") || arg.StartsWith("-closure:"))
            {
                string closure = arg.Substring("/closure:".Length);
                closure = closure.Trim('"');
                closureRootPatterns.Add(closure);
                ReportUnreferencedAssemblies = true;
                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("/resolve:") || arg.StartsWith("-resolve:"))
            {
                string resolveDir = arg.Substring("/resolve:".Length);
                resolveDir = resolveDir.Trim('"');
                resolveDir = Path.GetFullPath(resolveDir);
                if (!Directory.Exists(resolveDir))
                {
                    Checker.WriteError($"Custom resolve directory doesn't exist: {resolveDir}");
                    return false;
                }

                CustomResolveDirectories.Add(resolveDir);
                AddRootDirectory(resolveDir);

                arguments.Remove(arg);
                continue;
            }

            if ((arg.StartsWith("/p:") || arg.StartsWith("-p:")) && arg.Length > 3)
            {
                string pattern = arg.Substring(3);
                pattern = pattern.Trim('"');

                if (pattern.Contains(';'))
                {
                    foreach (var sub in pattern.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        patterns.Add(sub.Trim());
                    }
                }
                else
                {
                    patterns.Add(pattern);
                }

                arguments.Remove(arg);
                continue;
            }

            if (arg.StartsWith("-") || arg.StartsWith("/"))
            {
                Checker.WriteError($"Unknown argument: {arg}");
                return false;
            }
        }

        if (ReplicateBindingRedirects)
        {
            if (arguments.Count == 0)
            {
                Checker.WriteError($"Specify a source app.config file and one or more destination app.config files for -replicateBindingRedirects");
                return false;
            }

            SourceAppConfig = Path.GetFullPath(arguments[0]);
            if (!File.Exists(SourceAppConfig))
            {
                Checker.WriteError($"File not found: {SourceAppConfig}");
                return false;
            }

            DestinationAppConfigs = arguments.Skip(1).Select(p => Path.GetFullPath(p)).ToArray();
            var nonExistingDestination = DestinationAppConfigs.FirstOrDefault(f => !File.Exists(f));
            if (nonExistingDestination != null)
            {
                Checker.WriteError($"File not found: {nonExistingDestination}");
                return false;
            }

            return true;
        }

        if (OutputExpectedWarnings || OutputNewWarnings || OutputSummary)
        {
            EnableDefaultOutput = false;
        }

        if (patterns.Count == 0)
        {
            patterns.Add("*.dll");
            patterns.Add("*.exe");
            patterns.Add("*.exe.config");
        }

        if (exclusions.Count == 0)
        {
            exclusions.Add("*.resources.dll");
        }

        includeExclude = new IncludeExcludePattern("", IncludeExcludePattern.Combine(exclusions.Select(e => IncludeExcludePattern.PrepareRegexPattern(e))));

        foreach (var arg in arguments.ToArray())
        {
            if (!AddInclusion(arg, currentDirectory))
            {
                Checker.WriteError($"Expected directory, file glob or pattern: {arg}");
                return false;
            }
        }

        if (inclusions.Count == 0)
        {
            if (Recursive)
            {
                AddInclusion(Path.Combine(currentDirectory, "**"), currentDirectory);
            }
            else
            {
                AddInclusion(currentDirectory, currentDirectory);
            }
        }

        return true;
    }

    private bool AddInclusion(string text, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim('"');

        text = text.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        inclusions.Add(text);

        bool windowsNetworkShare = false;
        bool startsWithDirectorySeparator = false;
        if (Checker.IsWindows)
        {
            if (text.StartsWith(@"\"))
            {
                startsWithDirectorySeparator = true;
                if (text.StartsWith(@"\\"))
                {
                    windowsNetworkShare = true;
                }
            }
        }
        else
        {
            if (text.StartsWith(Path.DirectorySeparatorChar))
            {
                startsWithDirectorySeparator = true;
            }
        }

        var parts = text.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        string root = null;

        if (windowsNetworkShare)
        {
            if (parts.Length < 2)
            {
                return false;
            }

            root = $@"\\{parts[0]}\{parts[1]}";
            parts = parts.Skip(2).ToArray();
        }
        else if (startsWithDirectorySeparator)
        {
            root = $"\\{parts[0]}";
            parts = parts.Skip(1).ToArray();
        }
        else if (parts[0] == "**")
        {
            root = currentDirectory;
        }
        else if (parts[0] == ".")
        {
            root = currentDirectory;
            parts = parts.Skip(1).ToArray();
        }
        else if (parts[0] == "..")
        {
            root = Path.GetDirectoryName(currentDirectory);
            parts = parts.Skip(1).ToArray();
        }
        else if (Checker.IsWindows && parts[0].Length == 2 && parts[0][1] == ':')
        {
            root = parts[0] + "\\";
            parts = parts.Skip(1).ToArray();

            while (parts.Length > 0 &&
                parts[0] is string first &&
                !first.Contains("*") &&
                !first.Contains("?") &&
                first != "." &&
                first != "..")
            {
                root = Path.Combine(root, first);
                parts = parts.Skip(1).ToArray();
            }
        }

        if (root == null)
        {
            root = currentDirectory;
        }

        if (root == null || !Directory.Exists(root))
        {
            return false;
        }

        AddRootDirectory(root);

        return AddFiles(root, parts.ToArray());
    }

    private void AddRootDirectory(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        root = root.TrimEnd(Path.DirectorySeparatorChar);
        rootDirectories.Add(root);
    }

    private bool AddFiles(string root, string[] parts)
    {
        if (parts.Length == 0)
        {
            if (Recursive)
            {
                var dirs = Directory.GetDirectories(root);
                foreach (var dir in dirs)
                {
                    AddFiles(dir, parts);
                }
            }

            AddFilesInDirectory(root, patterns);
            return true;
        }

        if (parts[0] == "..")
        {
            root = Path.GetDirectoryName(root);
            if (root == null)
            {
                return false;
            }

            AddFilesInDirectory(root, parts.Skip(1).ToArray());
            return true;
        }

        var subdirectories = Directory.GetDirectories(root);

        string first = parts[0];
        if (first == "*" || first == "**")
        {
            if (first == "*")
            {
                parts = parts.Skip(1).ToArray();

                // when * is the last part, don't walk subfolders
                if (parts.Length == 0)
                {
                    return AddFiles(root, parts);
                }
            }
            else
            {
                if (!AddFiles(root, parts.Skip(1).ToArray()))
                {
                    return false;
                }
            }

            foreach (var subdirectory in subdirectories)
            {
                if (!AddFiles(subdirectory, parts))
                {
                    return false;
                }
            }

            return true;
        }

        if (subdirectories.FirstOrDefault(d => string.Equals(Path.GetFileName(d), first, PathComparison)) is string found)
        {
            return AddFiles(found, parts.Skip(1).ToArray());
        }

        if (parts.Length != 1)
        {
            return true;
        }

        string[] localPatterns = null;

        if (first.Contains(';'))
        {
            localPatterns = first.Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            localPatterns = new[] { first };
        }

        AddFilesInDirectory(root, localPatterns);

        return true;
    }

    private void AddFilesInDirectory(string root, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var filesInDirectory = Directory.GetFiles(root, pattern);
            foreach (var file in filesInDirectory)
            {
                if (includeExclude != null && includeExclude.Excludes(file))
                {
                    continue;
                }

                AddFile(file);
            }
        }
    }

    private void AddFile(string filePath)
    {
        if (IsClosureRoot(filePath))
        {
            closureRootFiles.Add(filePath);
        }
        else
        {
            files.Add(filePath);
        }

        string directory = Path.GetDirectoryName(filePath);
        allDirectories.Add(directory);
    }

    public static void PrintUsage()
    {
        Checker.WriteLine("https://github.com/KirillOsenkov/MetadataTools/tree/main/src/BinaryCompatChecker", ConsoleColor.Blue);
        Checker.WriteLine();
        Checker.Write(@"Usage: ", ConsoleColor.White);
        Checker.Write(@"checkbinarycompat", ConsoleColor.Cyan);
        Checker.Write(@" <file-spec>* <option>* @<response-file>*", ConsoleColor.White);
        Checker.WriteLine();
        Checker.Write(@"
Inspects assemblies and app.config files in a directory and reports potential issues.
Writes a report of possible issues to BinaryCompatReport.txt (sorted alphabetically).
File name/path can be customized using -out:<reportPath.txt>
If the file exists, it is used as a baseline.
If the report matches the baseline, it succeeds and returns exit code 0.
If the report doesn't match the baseline, the report is written and exit code is 1.
You can specify a separate baseline file using -baseline:<baselinePath.txt>
This way you can compare with the baseline and write the report to a different file.

File specs may be specified more than once. Each file spec is one of the following:");

        Checker.Write(@"

    * absolute directory path
    * directory relative to current directory
    * may include ** to indicate recursive subtree
    * may optionally end with:
        - a file name (a.dll)
        - a pattern such as *.dll
        - semicolon-separated patterns such as *.dll;*.exe;*.exe.config

    When no file-specs are specified, uses the current directory
    non-recursively. Pass -s for recursion.
    When no patterns are specified, uses *.dll;*.exe;*.dll.config;*.exe.config.");
        Checker.Write(@"

Options:", ConsoleColor.White);
        Checker.Write(@"
    All options with parameters (other than -out: and -ignore*) may be specified more than once.");
        Checker.Write(@"

    !<exclude-pattern>       Exclude a relative path or file pattern from analysis.
    -l                       Output list of visited assemblies to BinaryCompatReport.Assemblies.txt
    -s                       Recursive (visit specified directories recursively). Default is non-recursive.
    -closure:<file.dll>      Path to a root assembly of a closure (to report unused references).
    -resolve:<directory>     Additional directory to resolve reference assemblies from.
    -p:<pattern>             Semicolon-separated file pattern(s) such as *.dll;*.exe.
    -baseline:<baseline.txt> Optional, read <baseline.txt> instead of BinaryCompatReport.txt.
    -out:<report.txt>        Write report to <report.txt> instead of BinaryCompatReport.txt.

    -ignoreVersionMismatch   Do not report assembly version mismatches.
    -ignoreFacade            Do not report facade assemblies.
    -ignoreMissingAssemblies Do not report missing or unresolved assemblies.
    -ignoreMissingTypes      Do not report missing types.
    -ignoreMissingMembers    Do not report missing members.
    -ignoreInterfaces        Do not report missing interface implementations.
    -ivt                     Report internal API surface area consumed via InternalsVisibleTo.
    -embeddedInteropTypes    Report embedded interop types.
    -intPtrCtors             Report IntPtr constructors (Mono).

    If any of the below three output options are specified, all other output is suppressed.
    Use this to limit the output to only the information required.
    You can use any combination of these options:

    -outputExpectedWarnings  Only output old warnings that were in the baseline but no longer observed (and nothing else).
    -outputNewWarnings       Only output new warnings that weren't in the baseline and nothing else.
    -outputSummary           Output the summary comparing the report and the baseline.

    @response.rsp            Response file containing additional command-line arguments, one per line.
    -?:                      Display help.

-ignoreVersionMismatch can optionally specify a list of app.config file names (semicolon-separated)
that should be ignored for version mismatch reporting. If there is a version mismatch that is covered
by A.exe.config, but not B.exe.config, you can suppress warnings about B.exe.config using:
-ignoreVersionMismatch:B.exe.config

There is a separate command for the tool to replicate binding redirects from an app.config file
to one or more other app.config files:
");

        Checker.Write(@"checkbinarycompat", ConsoleColor.Cyan);
        Checker.Write(@" -replicateBindingRedirects <source.exe.config> <destination.exe.config>+", ConsoleColor.White);
        Checker.WriteLine();
    }
}