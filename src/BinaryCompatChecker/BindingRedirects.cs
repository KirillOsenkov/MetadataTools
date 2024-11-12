using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace BinaryCompatChecker;

public partial class Checker
{
    private void CheckAppConfigFiles(IEnumerable<string> appConfigFilePaths)
    {
        var versionMismatchesByName = versionMismatches
            .ToLookup(mismatch => mismatch.ExpectedReference.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.ToList(), StringComparer.OrdinalIgnoreCase);

        List<AppConfigFile> appConfigFiles = new();

        foreach (var appConfigFilePath in appConfigFilePaths)
        {
            Write(appConfigFilePath, ConsoleColor.Magenta);
            if (commandLine.IgnoreVersionMismatchForAppConfigs.Contains(Path.GetFileName(appConfigFilePath), StringComparer.OrdinalIgnoreCase))
            {
                Write(" - ignoring version mismatches", ConsoleColor.DarkMagenta);
            }

            WriteLine();

            var appConfigFileName = Path.GetFileName(appConfigFilePath);
            var appConfigFile = AppConfigFile.Read(appConfigFilePath);
            if (appConfigFile.Errors.Any())
            {
                foreach (var error in appConfigFile.Errors)
                {
                    diagnostics.Add($"App.config: '{appConfigFileName}': {error}");
                }
            }

            appConfigFiles.Add(appConfigFile);
        }

        var assemblies = this.resolveCache.Values
            .Concat(this.filePathToModuleDefinition.Values)
            .Where(v => v != null)
            .Distinct()
            .ToArray();

        foreach (var appConfigFile in appConfigFiles)
        {
            foreach (var bindingRedirect in appConfigFile.BindingRedirects)
            {
                CheckBindingRedirect(
                    appConfigFile.FileName,
                    bindingRedirect,
                    assemblies,
                    versionMismatchesByName);
            }
        }

        if (commandLine.ReportVersionMismatch)
        {
            ReportVersionMismatches(appConfigFiles, versionMismatchesByName);
        }
    }

    private void CheckBindingRedirect(
        string appConfigFileName,
        AppConfigFile.BindingRedirect bindingRedirect,
        IReadOnlyList<AssemblyDefinition> assemblies,
        Dictionary<string, List<VersionMismatch>> versionMismatchesByName)
    {
        string name = bindingRedirect.Name;
        string publicKeyToken = bindingRedirect.PublicKeyToken;
        Version oldVersionStart = bindingRedirect.OldVersionRangeStart;
        Version oldVersionEnd = bindingRedirect.OldVersionRangeEnd;
        Version newVersion = bindingRedirect.NewVersion;
        var codeBases = bindingRedirect.CodeBases;

        bool foundNewVersion = false;
        var foundVersions = new List<Version>();

        foreach (var assembly in assemblies)
        {
            if (!string.Equals(assembly.Name?.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assemblyVersion = assembly.Name.Version;

            foundVersions.Add(assemblyVersion);

            if (assemblyVersion == newVersion)
            {
                foundNewVersion = true;
                var actualToken = BitConverter.ToString(assembly.Name.PublicKeyToken).Replace("-", "").ToLowerInvariant();
                if (string.IsNullOrEmpty(actualToken))
                {
                    actualToken = "null";
                }

                if (!string.Equals(actualToken, publicKeyToken, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add($"App.config: '{appConfigFileName}': publicKeyToken '{publicKeyToken}' from bindingRedirect for {name} doesn't match one from the actual assembly: '{actualToken}'");
                }

                continue;
            }

            if (assemblyVersion < oldVersionStart)
            {
                diagnostics.Add($"App.config: '{appConfigFileName}': '{assembly.FullName}' version is less than bindingRedirect range start '{oldVersionStart}'");
                continue;
            }

            if (assemblyVersion > oldVersionEnd)
            {
                diagnostics.Add($"App.config: '{appConfigFileName}': '{assembly.FullName}' version is higher than bindingRedirect range end '{oldVersionEnd}'");
                continue;
            }
        }

        if (versionMismatchesByName.TryGetValue(name, out List<VersionMismatch> mismatches))
        {
            for (int i = mismatches.Count - 1; i >= 0; i--)
            {
                var versionMismatch = mismatches[i];

                bool handled = false;
                string diagnostic = null;

                var actualVersion = versionMismatch.ActualAssembly.Name.Version;
                var expectedVersion = versionMismatch.ExpectedReference.Version;

                bool expectedIsInRange = expectedVersion >= oldVersionStart && expectedVersion <= oldVersionEnd;

                if (expectedVersion < oldVersionStart && expectedVersion != actualVersion)
                {
                    diagnostic = $"App.config: '{appConfigFileName}': '{versionMismatch.Referencer.Name}' references '{versionMismatch.ExpectedReference.FullName}' which is lower than bindingRedirect range start '{oldVersionStart}' and not equal to actual version '{actualVersion}'";
                }
                else if (expectedVersion > oldVersionEnd && expectedVersion != actualVersion)
                {
                    diagnostic = $"App.config: '{appConfigFileName}': '{versionMismatch.Referencer.Name}' references '{versionMismatch.ExpectedReference.FullName}' which is higher than bindingRedirect range end '{oldVersionEnd}' and not equal to actual version '{actualVersion}'";
                }

                if (expectedIsInRange && foundNewVersion)
                {
                    handled = true;
                }

                if (diagnostic != null)
                {
                    diagnostics.Add(diagnostic);
                }

                if (handled)
                {
                    if (!versionMismatch.HandledByAppConfigs.Contains(appConfigFileName))
                    {
                        versionMismatch.HandledByAppConfigs.Add(appConfigFileName);
                    }
                }
            }
        }

        if (!foundNewVersion)
        {
            var message = $"App.config: '{appConfigFileName}': couldn't find assembly '{name}' with version {newVersion}.";
            if (foundVersions.Count > 0)
            {
                message += $" Found versions: {string.Join(",", foundVersions.Select(v => v.ToString()).Distinct().OrderBy(s => s))}";
            }

            diagnostics.Add(message);
        }
    }
}