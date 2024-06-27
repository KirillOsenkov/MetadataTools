using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

class ListBinaryInfo
{
    private static void PrintUsage()
    {
        Console.WriteLine(@"Usage:
lbi.exe [<pattern>]
        [-l[:<out.txt>]]
        [-d:<path>]*
        [-ed:<path>]*
        [-ef:<substring>]*
        [-nr]
        [-mo]
        [-sn]
        [-p]
        [-v]
        [-fv]
        [-iv]
        [-tf]
        [@response.rsp]

    -l:     List full directory contents (optionally output to a file, e.g. out.txt)
            If not specified, files are grouped by hash, then version.
    -d:     Specify root directory to start in (defaults to current directory).
            Maybe be specified more than once to scan multiple directories.
    -ed:    Exclude directory from search. May be specified more than once.
    -ef:    Exclude files with substring. May be specified more than once.
    -nr:    Non-recursive (current directory only). Recursive by default.
    -mo     Managed assemblies only.

    -sn     Print assembly strong named/delay-signed/public-signed.
    -snv    Validate assembly strong name using sn.exe -vf (slow).
    -p      Print assembly platform.
    -v      Print assembly version.
    -fv     Print assembly file version.
    -iv     Print assembly informational version.
    -tf     Print assembly target framework.

    @r:     Specify a response file (each file line treated as argument).

Examples: 
    lbi foo.dll
    lbi *.exe -nr
    lbi
    lbi -d:sub\directory -d:sub\dir2 -ed:sub\dir2\obj -l:out.txt");
    }

    private static string[] netfxToolsPaths =
    {
        @"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools",
        @"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.7.2 Tools",
        @"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.7.1 Tools",
        @"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.7 Tools",
        @"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.6.2 Tools",
        @"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.6.1 Tools",
        @"Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.6 Tools",
        @"Microsoft SDKs\Windows\v8.1A\bin\NETFX 4.5.1 Tools",
        @"Microsoft SDKs\Windows\v7.0A\bin",
    };

    private static string corflagsExe;
    private static string snExe;
    private static bool printSn;
    private static bool validateSn;
    private static bool checkPlatform;
    private static bool printVersion;
    private static bool printFileVersion;
    private static bool printInformationalVersion;
    private static bool printTargetFramework;
    private static bool managedOnly = false;

    static void Main(string[] args)
    {
        List<string> roots = new();
        List<string> excludeDirectories = new();
        List<string> excludeFileSubstrings = new();

        string patternList = "*.dll;*.exe";
        bool recursive = true;
        bool directoryListing = false;
        string outputFile = null;

        var arguments = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);

        while (arguments.FirstOrDefault(a => a.StartsWith("@")) is string responseFile)
        {
            arguments.Remove(responseFile);
            responseFile = responseFile.Substring(1);
            if (File.Exists(responseFile))
            {
                var lines = File.ReadAllLines(responseFile);
                foreach (var line in lines)
                {
                    arguments.Add(line);
                }
            }
            else
            {
                Error("Response file doesn't exist: " + responseFile);
                return;
            }
        }

        var helpArgument = arguments.FirstOrDefault(a => a == "/?" || a == "-?" || a == "-h" || a == "/h" || a == "-help" || a == "/help");
        if (helpArgument != null)
        {
            PrintUsage();
            return;
        }

        var nonRecursiveArgument = arguments.FirstOrDefault(a => a == "/nr" || a == "-nr");
        if (nonRecursiveArgument != null)
        {
            arguments.Remove(nonRecursiveArgument);
            recursive = false;
        }

        var managedOnlyArgument = arguments.FirstOrDefault(a => a == "-mo" || a == "/mo");
        if (managedOnlyArgument != null)
        {
            arguments.Remove(managedOnlyArgument);
            managedOnly = true;
        }

        var listArgument = arguments.FirstOrDefault(a => a.StartsWith("-l"));
        if (listArgument != null)
        {
            arguments.Remove(listArgument);
            directoryListing = true;
            patternList = "*";

            if (listArgument.StartsWith("-l:"))
            {
                string output = listArgument.Substring(3);
                output = output.Trim('"');
                outputFile = Path.GetFullPath(output);
            }
        }

        var signArgument = arguments.FirstOrDefault(a => a == "-sn");
        if (signArgument != null)
        {
            arguments.Remove(signArgument);
            printSn = true;
        }

        var snvArgument = arguments.FirstOrDefault(a => a == "-snv");
        if (snvArgument != null)
        {
            arguments.Remove(snvArgument);
            FindCorflagsAndSn();
            validateSn = snExe != null;
        }

        var platformArgument = arguments.FirstOrDefault(a => a == "-p");
        if (platformArgument != null)
        {
            arguments.Remove(platformArgument);
            FindCorflagsAndSn();
            checkPlatform = corflagsExe != null;
        }

        var versionArgument = arguments.FirstOrDefault(a => a == "-v");
        if (versionArgument != null)
        {
            arguments.Remove(versionArgument);
            printVersion = true;
        }

        var fileVersionArgument = arguments.FirstOrDefault(a => a == "-fv");
        if (fileVersionArgument != null)
        {
            arguments.Remove(fileVersionArgument);
            printFileVersion = true;
        }

        var informationalVersionArgument = arguments.FirstOrDefault(a => a == "-iv");
        if (informationalVersionArgument != null)
        {
            arguments.Remove(informationalVersionArgument);
            printInformationalVersion = true;
        }

        var targetFrameworkArgument = arguments.FirstOrDefault(a => a == "-tf");
        if (targetFrameworkArgument != null)
        {
            arguments.Remove(targetFrameworkArgument);
            printTargetFramework = true;
        }

        while (arguments.FirstOrDefault(a => a.StartsWith("-d:")) is string directoryArgument)
        {
            arguments.Remove(directoryArgument);
            string path = directoryArgument.Substring(3).Trim('"');
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                roots.Add(path);
            }
            else
            {
                Error($"Directory {path} doesn't exist");
                return;
            }
        }

        while (arguments.FirstOrDefault(a => a.StartsWith("-ed:")) is string directoryArgument)
        {
            arguments.Remove(directoryArgument);
            string path = directoryArgument.Substring(4).Trim('"');
            path = path.TrimEnd('\\');
            path = Path.GetFullPath(path);
            if (Directory.Exists(path))
            {
                excludeDirectories.Add(path);
            }
        }

        while (arguments.FirstOrDefault(a => a.StartsWith("-ef:")) is string excludeFileArgument)
        {
            arguments.Remove(excludeFileArgument);
            string substring = excludeFileArgument.Substring(4).Trim('"');
            excludeFileSubstrings.Add(substring);
        }

        if (arguments.FirstOrDefault(a => a.StartsWith("-") || a.StartsWith("/")) is string unknownArgument)
        {
            Error($"Unknown argument: {unknownArgument}");
            return;
        }

        if (arguments.Count > 0)
        {
            if (arguments.Count == 1)
            {
                string firstArgument = arguments.First().Trim('"');
                patternList = firstArgument;
            }
            else
            {
                PrintUsage();
                return;
            }
        }

        if (Directory.Exists(patternList))
        {
            roots.Add(Path.GetFullPath(patternList));
            if (directoryListing)
            {
                patternList = "*";
            }
            else
            {
                patternList = "*.dll;*.exe";
            }
        }
        else if (roots.Count == 0)
        {
            roots.Add(Environment.CurrentDirectory);
        }

        var files = new List<string>();
        if (File.Exists(patternList))
        {
            var file = Path.GetFullPath(patternList);
            if (!managedOnly || FileInfo.GetIsManagedAssembly(file))
            {
                files.Add(file);
            }

            roots.Clear();
        }
        else
        {
            var patterns = patternList.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            Func<string, bool> exclude = null;
            if (excludeDirectories.Count > 0)
            {
                var hashset = new HashSet<string>(excludeDirectories, StringComparer.OrdinalIgnoreCase);
                exclude = hashset.Contains;
            }

            foreach (var root in roots)
            {
                AddFiles(
                    root,
                    patterns,
                    files,
                    recursive,
                    managedOnly,
                    exclude,
                    excludeFileSubstrings);
            }
        }

        if (directoryListing)
        {
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (!root.EndsWith(Path.DirectorySeparatorChar))
                {
                    roots[i] = root + Path.DirectorySeparatorChar;
                }
            }

            PrintFiles(roots, files, outputFile);
            return;
        }

        PrintGroupedFiles(files);
    }

    private static void AddFiles(
        string directory,
        string[] patterns,
        List<string> list,
        bool recursive,
        bool managedOnly,
        Func<string, bool> excludeDirectory,
        List<string> excludeFileSubstrings)
    {
        if (excludeDirectory != null && excludeDirectory(directory))
        {
            return;
        }

        if (recursive)
        {
            try
            {
                var directories = Directory.GetDirectories(directory);
                foreach (var subdirectory in directories)
                {
                    AddFiles(subdirectory,
                        patterns,
                        list,
                        recursive,
                        managedOnly,
                        excludeDirectory,
                        excludeFileSubstrings);
                }
            }
            catch
            {
            }
        }

        foreach (var pattern in patterns)
        {
            try
            {
                var files = Directory.GetFiles(directory, pattern);
                foreach (var file in files)
                {
                    string name = Path.GetFileName(file);

                    if (ShouldExcludeFile(name, excludeFileSubstrings))
                    {
                        continue;
                    }

                    if (managedOnly && !FileInfo.GetIsManagedAssembly(file))
                    {
                        continue;
                    }

                    list.Add(file);
                }
            }
            catch
            {
            }
        }
    }

    private static bool ShouldExcludeFile(string name, List<string> excludeFileSubstrings)
    {
        foreach (var substring in excludeFileSubstrings)
        {
            if (name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void PrintFiles(IList<string> rootDirectories, List<string> files, string outputFile)
    {
        var sb = new StringBuilder();

        bool checkForManagedAssembly =
            printVersion ||
            printTargetFramework ||
            printSn ||
            validateSn ||
            checkPlatform ||
            printFileVersion ||
            printInformationalVersion;

        var infos = new List<FileInfo>(files.Count);

        for (int i = 0; i < files.Count; i++)
        {
            string filePath = files[i];
            string relativePath = ComputeRelativePath(rootDirectories, filePath);

            var fileInfo = FileInfo.Get(filePath, isConfirmedManagedAssembly: managedOnly);
            fileInfo.Text = relativePath;

            infos.Add(fileInfo);
        }

        if (checkForManagedAssembly)
        {
            bool parallel = true;
            if (parallel)
            {
                Parallel.ForEach(infos, info =>
                {
                    PopulateText(info);
                });
            }
            else
            {
                foreach (var info in infos)
                {
                    PopulateText(info);
                }
            }
        }

        foreach (var file in infos)
        {
            sb.AppendLine(file.Text);
        }

        string text = sb.ToString();

        if (!string.IsNullOrEmpty(outputFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
            File.WriteAllText(outputFile, text);
        }
        else
        {
            Console.Write(text);
        }
    }

    private static void PopulateText(FileInfo fileInfo)
    {
        fileInfo.Text = GetTextLine(fileInfo);
    }

    [ThreadStatic]
    private static StringBuilder sb = null;

    private static string GetTextLine(FileInfo fileInfo)
    {
        if (sb == null)
        {
            sb = new StringBuilder();
        }

        sb.Clear();

        if (fileInfo.Text != null)
        {
            sb.Append(fileInfo.Text);
        }

        void AppendSeparator()
        {
            sb.Append(",");
        }

        if (fileInfo.IsManagedAssembly)
        {
            if (printVersion)
            {
                if (fileInfo.Version is string version)
                {
                    AppendSeparator();
                    sb.Append(version);
                }
            }

            if (printTargetFramework && !string.IsNullOrWhiteSpace(fileInfo.TargetFramework))
            {
                AppendSeparator();
                sb.Append(fileInfo.TargetFramework);
            }

            string signedText = fileInfo.GetSignedText(printSn, validateSn);
            if (!string.IsNullOrWhiteSpace(signedText))
            {
                AppendSeparator();
                sb.Append(signedText);
            }

            if (checkPlatform)
            {
                string platformText = fileInfo.PlatformText;
                if (!string.IsNullOrWhiteSpace(platformText))
                {
                    AppendSeparator();
                    sb.Append(platformText);
                }
            }

            if (printFileVersion && !string.IsNullOrWhiteSpace(fileInfo.FileVersion))
            {
                AppendSeparator();
                sb.Append(fileInfo.FileVersion);
            }

            if (printInformationalVersion && !string.IsNullOrWhiteSpace(fileInfo.InformationalVersion))
            {
                AppendSeparator();
                sb.Append(fileInfo.InformationalVersion);
            }
        }

        return sb.ToString();
    }

    private static string ComputeRelativePath(IList<string> rootDirectories, string filePath)
    {
        string relativePath = filePath;

        // make the path relative to the current root directory, if we have any root directories
        if (rootDirectories != null && rootDirectories.Count > 0)
        {
            string rootDirectory = rootDirectories[0];
            while (rootDirectory != null && !relativePath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                rootDirectories.RemoveAt(0);

                if (rootDirectories.Count > 0)
                {
                    rootDirectory = rootDirectories[0];
                }
                else
                {
                    rootDirectory = null;
                }
            }

            if (rootDirectory != null)
            {
                relativePath = relativePath.Substring(rootDirectory.Length);
            }
        }

        return relativePath;
    }

    private static void PrintGroupedFiles(List<string> files)
    {
        var fileGroups = files
            .Select(f => FileInfo.Get(f, isConfirmedManagedAssembly: managedOnly))
            .GroupBy(f => f.AssemblyName)
            .OrderBy(g => g.Key);

        foreach (var assemblyNameGroup in fileGroups)
        {
            Highlight(assemblyNameGroup.Key, ConsoleColor.Cyan);
            foreach (var shaGroup in assemblyNameGroup.GroupBy(f => f.Sha))
            {
                var fileInfo = shaGroup.First();
                Highlight("    SHA1: " + shaGroup.Key, ConsoleColor.DarkGray, newLineAtEnd: false);

                Highlight(" " + shaGroup.First().FileSize.ToString("N0"), ConsoleColor.Gray, newLineAtEnd: false);

                if (fileInfo.AssemblyName != null)
                {
                    if (printTargetFramework && !string.IsNullOrWhiteSpace(fileInfo.TargetFramework))
                    {
                        Highlight(" " + fileInfo.TargetFramework, ConsoleColor.Blue, newLineAtEnd: false);
                    }

                    var signedText = fileInfo.GetSignedText(printSn, validateSn);
                    if (!string.IsNullOrEmpty(signedText))
                    {
                        var color = ConsoleColor.DarkGray;
                        if (signedText == "Strong name validation failed")
                        {
                            color = ConsoleColor.Red;
                        }

                        Highlight($" {signedText}", color, newLineAtEnd: false);
                    }

                    if (checkPlatform)
                    {
                        var platformText = fileInfo.PlatformText;
                        if (!string.IsNullOrEmpty(platformText))
                        {
                            Highlight(" " + platformText, ConsoleColor.DarkMagenta, newLineAtEnd: false);
                        }
                    }

                    if (printVersion && !string.IsNullOrWhiteSpace(fileInfo.Version))
                    {
                        Highlight(" " + fileInfo.Version, ConsoleColor.DarkCyan, newLineAtEnd: false);
                    }

                    if (printFileVersion && !string.IsNullOrWhiteSpace(fileInfo.FileVersion))
                    {
                        Highlight(" " + fileInfo.FileVersion, ConsoleColor.DarkYellow, newLineAtEnd: false);
                    }

                    if (printInformationalVersion && !string.IsNullOrWhiteSpace(fileInfo.InformationalVersion))
                    {
                        Highlight(" " + fileInfo.InformationalVersion, ConsoleColor.DarkGreen, newLineAtEnd: false);
                    }
                }

                Console.WriteLine();

                foreach (var file in shaGroup.OrderBy(f => f.FilePath))
                {
                    Highlight("        " + file.FilePath, ConsoleColor.White);
                }
            }
        }
    }

    private static bool searchedForCorflagsAndSn;
    private static void FindCorflagsAndSn()
    {
        if (searchedForCorflagsAndSn)
        {
            return;
        }

        searchedForCorflagsAndSn = true;

        foreach (var netfxToolsPath in netfxToolsPaths)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                netfxToolsPath,
                "corflags.exe");
            if (corflagsExe == null && File.Exists(path))
            {
                corflagsExe = path;
            }

            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                netfxToolsPath,
                @"sn.exe");
            if (snExe == null && File.Exists(path))
            {
                snExe = path;
            }

            if (corflagsExe != null && snExe != null)
            {
                break;
            }
        }
    }

    public const string NotAManagedAssembly = "Not a managed assembly";

    private static AssemblyName GetAssemblyName(string file)
    {
        try
        {
            var name = AssemblyName.GetAssemblyName(file);
            return name;
        }
        catch
        {
            return null;
        }
    }

    public static string GetAssemblyNameText(string file)
    {
        var name = GetAssemblyName(file);
        return name?.ToString();
    }

    public static void CheckPlatform(FileInfo fileInfo)
    {
        StartProcess(corflagsExe, "/nologo " + fileInfo.FilePath.QuoteIfNeeded(), fileInfo);
    }

    public static void CheckSigned(FileInfo fileInfo)
    {
        StartProcess(snExe, "-vf " + fileInfo.FilePath.QuoteIfNeeded(), fileInfo);
    }

    private static void StartProcess(string executableFilePath, string arguments, FileInfo fileInfo)
    {
        if (!File.Exists(executableFilePath))
        {
            return;
        }

        var processResult = ProcessRunner.Run(executableFilePath, arguments);
        var text = processResult.Output;
        var lines = text.GetLines();
        foreach (var line in lines)
        {
            ProcessLine(fileInfo, line);
        }
    }

    private static void ProcessLine(FileInfo fileInfo, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // corflags
        if (text.Contains("32BITPREF"))
        {
            fileInfo.Platform = text;
            return;
        }

        if (text.Contains("32BITREQ  : 1"))
        {
            fileInfo.Architecture = "x86";
            return;
        }

        if (text.Contains("32BITREQ  : 0"))
        {
            fileInfo.Architecture = "Any CPU";
            return;
        }

        // sn
        if (text.Contains("Copyright") ||
            text.Contains("(R)") ||
            text.Contains("Version ") ||
            text.Contains("CLR Header") ||
            text.Contains("PE  ") ||
            text.Contains("ILONLY  ") ||
            text.Contains("CorFlags") ||
            text.Contains("does not represent") ||
            text.Contains("is verified with a key other than the identity key") ||
            text.Contains("The specified file does not have a valid managed header"))
        {
            return;
        }

        if (text.Contains("is valid"))
        {
            fileInfo.FullSigned = "Full-signed";
            return;
        }

        if (text.Contains("is a delay-signed or test-signed"))
        {
            fileInfo.FullSigned = "Delay-signed or test-signed";
            return;
        }

        if (text.Contains("Failed to verify assembly -- Strong name validation failed."))
        {
            fileInfo.FullSigned = "Strong name validation failed";
            return;
        }

        if (text.Contains("Signed    : 1"))
        {
            fileInfo.Signed = "Signed";
            return;
        }

        if (text.Contains("Signed    : 0"))
        {
            fileInfo.Signed = "Unsigned";
            return;
        }

        Console.WriteLine(text);
    }

    private static void Error(string text)
    {
        Highlight(text, ConsoleColor.Red, writer: Console.Error);
    }

    private static void Highlight(
        string message,
        ConsoleColor color = ConsoleColor.Cyan,
        bool newLineAtEnd = true,
        TextWriter writer = null)
    {
        writer ??= Console.Out;

        lock (typeof(Console))
        {
            var oldColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            writer.Write(message);
            if (newLineAtEnd)
            {
                writer.WriteLine();
            }

            Console.ForegroundColor = oldColor;
        }
    }
}