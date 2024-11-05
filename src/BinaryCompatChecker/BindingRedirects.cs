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
            WriteLine(appConfigFilePath, ConsoleColor.DarkYellow);

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
                    bindingRedirect.Name,
                    bindingRedirect.PublicKeyToken,
                    bindingRedirect.OldVersionRangeStart,
                    bindingRedirect.OldVersionRangeEnd,
                    bindingRedirect.NewVersion,
                    assemblies,
                    versionMismatchesByName);
            }
        }

        if (commandLine.ReportVersionMismatch)
        {
            ReportVersionMismatches(versionMismatchesByName);
        }
    }

    private void CheckBindingRedirect(
        string appConfigFileName,
        string name,
        string publicKeyToken,
        Version oldVersionStart,
        Version oldVersionEnd,
        Version newVersion,
        IReadOnlyList<AssemblyDefinition> assemblies,
        Dictionary<string, List<VersionMismatch>> versionMismatchesByName)
    {
        bool foundNewVersion = false;
        var foundVersions = new List<Version>();

        foreach (var assembly in assemblies)
        {
            if (assembly == null)
            {
                continue;
            }

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
            versionMismatchesByName.Remove(name);
            for (int i = mismatches.Count - 1; i >= 0; i--)
            {
                var versionMismatch = mismatches[i];

                var actualVersion = versionMismatch.ActualAssembly.Name.Version;
                if (actualVersion != newVersion)
                {
                    string diagnostic = null;

                    if (actualVersion < oldVersionStart)
                    {
                        diagnostic = $"App.config: '{appConfigFileName}': '{versionMismatch.ActualAssembly.FullName}' version is less than bindingRedirect range start '{oldVersionStart}' (Expected by '{versionMismatch.Referencer.Name}')";
                    }
                    else if (actualVersion > oldVersionEnd)
                    {
                        diagnostic = $"App.config: '{appConfigFileName}': '{versionMismatch.ActualAssembly.FullName}' version is higher than bindingRedirect range end '{oldVersionEnd}' (Expected by '{versionMismatch.Referencer.Name}')";
                    }

                    if (diagnostic != null)
                    {
                        diagnostics.Add(diagnostic);
                        continue;
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