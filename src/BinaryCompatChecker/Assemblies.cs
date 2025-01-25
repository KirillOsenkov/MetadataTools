using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;

namespace BinaryCompatChecker;

public partial class Checker
{
    IAssemblyResolver resolver;
    string currentResolveDirectory;

    private class VersionMismatch
    {
        public AssemblyDefinition Referencer;
        public AssemblyNameReference ExpectedReference;
        public AssemblyDefinition ActualAssembly;

        public List<string> HandledByAppConfigs { get; } = new();
    }

    public class IVTUsage
    {
        public string ExposingAssembly { get; set; }
        public string ConsumingAssembly { get; set; }
        public string Member { get; set; }
    }

    private readonly List<VersionMismatch> versionMismatches = new List<VersionMismatch>();

    private readonly HashSet<string> resolvedFromFramework = new HashSet<string>(CommandLine.PathComparer);

    public class CustomAssemblyResolver : BaseAssemblyResolver
    {
        private readonly Checker checker;
        private readonly Dictionary<string, AssemblyDefinition> resolutionCache = new();

        public CustomAssemblyResolver(Checker checker)
        {
            this.checker = checker;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack(); // see https://github.com/KirillOsenkov/MetadataTools/issues/4

            if (resolutionCache.TryGetValue(name.FullName, out var resolved))
            {
                return resolved;
            }

            resolved = checker.Resolve(name);
            resolved = resolved ?? base.Resolve(name, parameters);
            resolutionCache[name.FullName] = resolved;

            return resolved;
        }
    }

    private void OnAssemblyLoaded(AssemblyDefinition assemblyDefinition)
    {
        string filePath = assemblyDefinition.MainModule.FileName;
        if (commandLine.EnableDefaultOutput)
        {
            WriteLine(filePath, ConsoleColor.DarkGray);
        }
    }

    private string GetResolveKey(string referenceFullName) => currentResolveDirectory + "\\" + referenceFullName;

    private AssemblyDefinition Resolve(AssemblyNameReference reference)
    {
        lock (resolveCache)
        {
            string resolveKey = GetResolveKey(reference.FullName);
            if (resolveCache.TryGetValue(resolveKey, out AssemblyDefinition result))
            {
                return result;
            }

            string filePath = TryResolve(reference);

            if (File.Exists(filePath))
            {
                result = Load(filePath);
                resolveCache[resolveKey] = result;
            }
            else
            {
                resolveCache[resolveKey] = null;
            }

            return result;
        }
    }

    private string TryResolve(AssemblyNameReference reference)
    {
        string result;

        // first try to see if we already have the exact version loaded
        result = TryResolveFromLoadedAssemblies(reference, strictVersion: true);
        if (result != null)
        {
            return result;
        }

        result = TryResolveFromInputFiles(reference);
        if (result != null)
        {
            return result;
        }

        result = TryResolveFromCodeBase(reference);
        if (result != null)
        {
            return result;
        }

        if (commandLine.ResolveFromFramework)
        {
            result = TryResolveFromFramework(reference);
            if (result != null)
            {
                return result;
            }
        }

        result = TryResolveFromCustomDirectories(reference);
        if (result != null)
        {
            return result;
        }

        // try any version as the last resort
        result = TryResolveFromLoadedAssemblies(reference, strictVersion: false);
        if (result != null)
        {
            return result;
        }

        return null;
    }

    private string TryResolveFromLoadedAssemblies(AssemblyNameReference reference, bool strictVersion = true)
    {
        var result = assemblyCache.TryResolve(reference, strictVersion);
        return result;
    }

    private string TryResolveFromInputFiles(AssemblyNameReference reference)
    {
        foreach (var file in commandLine.Files)
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), reference.Name, StringComparison.OrdinalIgnoreCase))
            {
                var assemblyDefinition = Load(file);
                if (assemblyDefinition != null && !Framework.IsFacadeAssembly(assemblyDefinition))
                {
                    return file;
                }
            }
        }

        foreach (var directory in commandLine.AllDirectories)
        {
            string candidate = Path.Combine(directory, reference.Name + ".dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string TryResolveFromCodeBase(AssemblyNameReference reference)
    {
        foreach (var appConfig in appConfigFiles)
        {
            if (!appConfig.HasCodeBases)
            {
                continue;
            }

            if (!CommandLine.PathComparer.Equals(appConfig.Directory, currentResolveDirectory))
            {
                continue;
            }

            foreach (var bindingRedirect in appConfig.BindingRedirects)
            {
                if (!string.Equals(bindingRedirect.Name, reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resolvedVersion = reference.Version;
                if (bindingRedirect.OldVersionRangeStart != null &&
                    bindingRedirect.OldVersionRangeEnd != null &&
                    bindingRedirect.NewVersion != null &&
                    resolvedVersion >= bindingRedirect.OldVersionRangeStart &&
                    resolvedVersion <= bindingRedirect.OldVersionRangeEnd)
                {
                    resolvedVersion = bindingRedirect.NewVersion;
                }

                foreach (var codeBase in bindingRedirect.CodeBases)
                {
                    if (resolvedVersion == codeBase.Version)
                    {
                        var assemblyDefinition = codeBase.AssemblyDefinition ??= Load(codeBase.FilePath);
                        if (assemblyDefinition != null &&
                            string.Equals(assemblyDefinition.Name.Name, reference.Name, StringComparison.OrdinalIgnoreCase) &&
                            assemblyDefinition.Name.Version == resolvedVersion)
                        {
                            return codeBase.FilePath;
                        }
                    }
                }
            }
        }

        return null;
    }

    private string TryResolveFromFramework(AssemblyNameReference reference)
    {
        string shortName = reference.Name;
        Version version = reference.Version;

        bool isFrameworkName = Framework.IsFrameworkName(shortName);
        if (!isFrameworkName)
        {
            return null;
        }

        bool desktop = false;
        bool isFrameworkRedirect = false;

        if (IsWindows)
        {
            // 4.0.1.0 is .NETPortable,Version=v5.0, still resolve from desktop
            if (version <= new Version(4, 0, 10, 0))
            {
                desktop = true;
            }

            if (Framework.TryGetFrameworkRedirect(reference.Name) is { } frameworkRedirectVersion && reference.Version <= frameworkRedirectVersion)
            {
                desktop = true;
                isFrameworkRedirect = true;
            }
        }

        // resolve desktop framework assemblies from the GAC
        if (desktop)
        {
            if (string.Equals(shortName, "mscorlib", StringComparison.OrdinalIgnoreCase) &&
                Framework.MscorlibFilePath is { } mscorlibFilePath &&
                File.Exists(mscorlibFilePath))
            {
                resolvedFromFramework.Add(mscorlibFilePath);
                return mscorlibFilePath;
            }

            if (!commandLine.ResolveFromGac)
            {
                return null;
            }

            foreach (var dir in Framework.DesktopNetFrameworkDirectories)
            {
                var combined = Path.Combine(dir, shortName);
                if (Directory.Exists(combined))
                {
                    var first = Directory.GetDirectories(combined);
                    foreach (var item in first)
                    {
                        try
                        {
                            var candidate = Path.Combine(item, shortName + ".dll");
                            if (File.Exists(candidate))
                            {
                                var fileVersion = AssemblyName.GetAssemblyName(candidate);
                                if (fileVersion != null)
                                {
                                    bool match = false;

                                    if (fileVersion.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (string.Equals(fileVersion.FullName, reference.FullName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            match = true;
                                        }
                                        else if (
                                            reference.Version.Major == 0 &&
                                            reference.Version.Minor == 0 &&
                                            reference.Version.Build == 0 &&
                                            reference.Version.Revision == 0)
                                        {
                                            match = true;
                                        }
                                        else if (isFrameworkRedirect)
                                        {
                                            match = true;
                                        }
                                        else if (reference.IsRetargetable)
                                        {
                                            match = true;
                                        }
                                    }

                                    if (match)
                                    {
                                        resolvedFromFramework.Add(candidate);
                                        return candidate;
                                    }
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
        else
        {
            if (!commandLine.ResolveFromNetCore)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(Framework.DotnetRuntimeDirectory);

            string versionPrefix = version.Major.ToString();

            // .NETCore 3 has versions like 4.1.1.0 or 4.2.2.0, not confusingly
            if (version.Major == 4 &&
                (version.Minor == 1 || version.Minor == 2))
            {
                versionPrefix = "3";
            }

            var versionDirectories = Directory.GetDirectories(parent, versionPrefix + "*");
            if (versionDirectories.Length > 0)
            {
                var lastVersion = versionDirectories[versionDirectories.Length - 1];
                string versionCandidate = Path.Combine(lastVersion, shortName + ".dll");
                if (File.Exists(versionCandidate))
                {
                    resolvedFromFramework.Add(versionCandidate);
                    return versionCandidate;
                }
            }

            string frameworkCandidate = Path.Combine(Framework.DotnetRuntimeDirectory, shortName + ".dll");
            if (File.Exists(frameworkCandidate))
            {
                resolvedFromFramework.Add(frameworkCandidate);
                return frameworkCandidate;
            }
        }

        return null;
    }

    private string TryResolveFromCustomDirectories(AssemblyNameReference reference)
    {
        string shortName = reference.Name;

        foreach (var customResolveDirectory in commandLine.CustomResolveDirectories)
        {
            var candidate = Path.Combine(customResolveDirectory, shortName + ".dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private AssemblyDefinition Load(string filePath)
    {
        var result = assemblyCache.Load(filePath, resolver, diagnostics);

        if (result.assemblyDefinition is { } assemblyDefinition)
        {
            if (!result.fromCache)
            {
                OnAssemblyLoaded(assemblyDefinition);

                if (!Framework.IsNetFrameworkAssembly(assemblyDefinition))
                {
                    string relativePath = GetRelativePath(filePath);
                    string targetFramework = GetTargetFramework(assemblyDefinition);
                    if (targetFramework != null)
                    {
                        targetFramework = " " + targetFramework;
                    }

                    assembliesExamined.Add($"{relativePath}    {assemblyDefinition.Name.Version}{targetFramework}");
                }
            }

            assemblyDefinitionsExamined.Add(assemblyDefinition);
        }

        return result.assemblyDefinition;
    }

    private string GetRelativePath(string filePath)
    {
        foreach (var rootDirectory in commandLine.RootDirectories)
        {
            if (filePath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                filePath = filePath.Substring(rootDirectory.Length + 1);
            }
        }

        return filePath;
    }
}

public static class VersionExtensions
{
    public static bool Is(this Version version, int major = 0, int minor = 0, int build = 0, int revision = 0)
    {
        return version != null &&
            version.Major == major &&
            version.Minor == minor &&
            version.Build == build &&
            version.Revision == revision;
    }

    public static string GetString(this byte[] bytes)
    {
        if (bytes == null)
        {
            return null;
        }

        string result = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        return result;
    }

    public static string GetToken(this AssemblyNameDefinition assemblyName)
    {
        string result = assemblyName.PublicKeyToken.GetString();
        if (string.IsNullOrEmpty(result))
        {
            result = "null";
        }

        return result;
    }
}