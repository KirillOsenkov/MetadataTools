using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace BinaryCompatChecker;

public partial class Checker
{
    private void CheckAppConfigFiles(IReadOnlyList<AppConfigFile> appConfigFiles)
    {
        var versionMismatchesByName = versionMismatches
            .ToLookup(mismatch => mismatch.ExpectedReference.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var appConfigFile in appConfigFiles)
        {
            foreach (var bindingRedirect in appConfigFile.BindingRedirects)
            {
                CheckBindingRedirect(
                    appConfigFile,
                    bindingRedirect,
                    assemblyDefinitionsExamined,
                    versionMismatchesByName);
            }
        }

        if (commandLine.ReportVersionMismatch)
        {
            ReportVersionMismatches(appConfigFiles, versionMismatchesByName);
        }
    }

    private void CheckBindingRedirect(
        AppConfigFile appConfigFile,
        AppConfigFile.BindingRedirect bindingRedirect,
        IEnumerable<AssemblyDefinition> assemblies,
        Dictionary<string, List<VersionMismatch>> versionMismatchesByName)
    {
        string name = bindingRedirect.Name;
        string publicKeyToken = bindingRedirect.PublicKeyToken;
        Version oldVersionStart = bindingRedirect.OldVersionRangeStart;
        Version oldVersionEnd = bindingRedirect.OldVersionRangeEnd;
        Version newVersion = bindingRedirect.NewVersion;
        var codeBases = bindingRedirect.CodeBases;
        string appConfigFileName = appConfigFile.FileName;

        bool foundNewVersion = false;
        var foundVersions = new List<AssemblyNameDefinition>();

        foreach (var assembly in assemblies)
        {
            var assemblyName = assembly.Name;
            if (assemblyName == null)
            {
                continue;
            }

            if (!string.Equals(assemblyName.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foundVersions.Add(assemblyName);

            if (oldVersionStart != null && assemblyName.Version < oldVersionStart && !appConfigFile.IgnoreVersionMismatch)
            {
                diagnostics.Add($"App.config: '{appConfigFileName}': '{assembly.FullName}' version is less than bindingRedirect range start '{oldVersionStart}'");
                continue;
            }

            if (oldVersionEnd != null && assemblyName.Version > oldVersionEnd && !appConfigFile.IgnoreVersionMismatch)
            {
                diagnostics.Add($"App.config: '{appConfigFileName}': '{assembly.FullName}' version is higher than bindingRedirect range end '{oldVersionEnd}'");
                continue;
            }

            if (newVersion != null && assemblyName.Version == newVersion)
            {
                foundNewVersion = true;
            }
        }

        if (codeBases.Any())
        {
            foreach (var codeBase in codeBases)
            {
                codeBase.AssemblyDefinition ??= Load(codeBase.FilePath, markAsExamined: false);
                if (codeBase.AssemblyDefinition != null)
                {
                    if (!foundVersions.Contains(codeBase.AssemblyDefinition.Name))
                    {
                        foundVersions.Add(codeBase.AssemblyDefinition.Name);
                    }
                }
            }
        }

        if (versionMismatchesByName.TryGetValue(name, out List<VersionMismatch> mismatches))
        {
            for (int i = mismatches.Count - 1; i >= 0; i--)
            {
                var versionMismatch = mismatches[i];

                bool handled = false;
                string diagnostic = null;

                string actualPublicKeyToken = versionMismatch.ActualAssembly.Name.GetToken();
                if (!string.Equals(actualPublicKeyToken, publicKeyToken, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add($"App.config: '{appConfigFileName}': publicKeyToken '{publicKeyToken}' from bindingRedirect for {name} doesn't match one from the actual assembly: '{actualPublicKeyToken}'");
                }

                var actualVersion = versionMismatch.ActualAssembly.Name.Version;
                var expectedVersion = versionMismatch.ExpectedReference.Version;
                Version resolvedVersion = expectedVersion;

                bool isInRange = oldVersionStart != null &&
                    oldVersionEnd != null &&
                    resolvedVersion >= oldVersionStart &&
                    resolvedVersion <= oldVersionEnd;
                if (isInRange)
                {
                    resolvedVersion = newVersion;
                }

                if (resolvedVersion == actualVersion)
                {
                    handled = true;
                }

                if (!handled && FindVersionFromCodeBase(resolvedVersion))
                {
                    handled = true;
                    foundNewVersion = true;
                }

                if (!handled)
                {
                    if (oldVersionStart != null && resolvedVersion < oldVersionStart)
                    {
                        diagnostic = $"App.config: '{appConfigFileName}': '{versionMismatch.Referencer.Name}' references '{versionMismatch.ExpectedReference.FullName}' which is lower than bindingRedirect range start '{oldVersionStart}' and not equal to actual version '{actualVersion}'";
                    }
                    else if (oldVersionEnd != null && resolvedVersion > oldVersionEnd)
                    {
                        diagnostic = $"App.config: '{appConfigFileName}': '{versionMismatch.Referencer.Name}' references '{versionMismatch.ExpectedReference.FullName}' which is higher than bindingRedirect range end '{oldVersionEnd}' and not equal to actual version '{actualVersion}'";
                    }
                    else
                    {
                    }
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

        if (!foundNewVersion && newVersion != null && FindVersionFromCodeBase(newVersion))
        {
            foundNewVersion = true;
        }

        if (!foundNewVersion)
        {
            string withVersion = "";
            if (newVersion != null)
            {
                withVersion = $" with version {newVersion}";
            }

            var message = $"App.config: '{appConfigFileName}': couldn't find assembly '{name}'{withVersion}.";
            if (foundVersions.Count > 0)
            {
                message += $" Found versions: {string.Join(",", foundVersions.Select(v => v.Version.ToString()).Distinct().OrderBy(s => s))}";
            }

            diagnostics.Add(message);
        }

        bool FindVersionFromCodeBase(Version requestedVersion)
        {
            if (requestedVersion == null || !codeBases.Any())
            {
                return false;
            }

            var match = codeBases.FirstOrDefault(c => c.Version == requestedVersion);
            if (match != null)
            {
                match.AssemblyDefinition ??= Load(match.FilePath, markAsExamined: false);
                if (match.AssemblyDefinition != null && match.AssemblyDefinition.Name.Version == requestedVersion)
                {
                    return true;
                }
            }

            return false;
        }
    }
}