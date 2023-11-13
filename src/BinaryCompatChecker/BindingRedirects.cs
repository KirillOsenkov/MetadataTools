using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Collections.Generic;

namespace BinaryCompatChecker;

public partial class Checker
{
    private void CheckAppConfigFiles(IEnumerable<string> appConfigFiles)
    {
        var versionMismatchesByName = versionMismatches
            .ToLookup(mismatch => mismatch.ExpectedReference.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var appConfigFilePath in appConfigFiles)
        {
            var appConfigFileName = Path.GetFileName(appConfigFilePath);
            var appConfigFile = AppConfigFile.Read(appConfigFilePath);
            if (appConfigFile.Errors.Any())
            {
                foreach (var error in appConfigFile.Errors)
                {
                    diagnostics.Add($"In app.config file {appConfigFileName}: {error}");
                }
            }

            foreach (var bindingRedirect in appConfigFile.BindingRedirects)
            {
                CheckBindingRedirect(
                    appConfigFileName,
                    bindingRedirect.Name,
                    bindingRedirect.PublicKeyToken,
                    bindingRedirect.OldVersionRangeStart,
                    bindingRedirect.OldVersionRangeEnd,
                    bindingRedirect.NewVersion,
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
        Dictionary<string, List<VersionMismatch>> versionMismatchesByName)
    {
        bool foundNewVersion = false;
        var foundVersions = new List<Version>();

        foreach (var kvp in this.filePathToModuleDefinition)
        {
            var assembly = kvp.Value;
            if (assembly == null)
            {
                continue;
            }

            if (!string.Equals(assembly.Name?.Name, name))
            {
                continue;
            }

            foundVersions.Add(assembly.Name.Version);

            if (assembly.Name.Version == newVersion)
            {
                foundNewVersion = true;
                var actualToken = BitConverter.ToString(assembly.Name.PublicKeyToken).Replace("-", "").ToLowerInvariant();
                if (string.IsNullOrEmpty(actualToken))
                {
                    actualToken = "null";
                }

                if (!string.Equals(actualToken, publicKeyToken, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add($"In {appConfigFileName}: publicKeyToken '{publicKeyToken}' from bindingRedirect for {name} doesn't match one from the actual assembly: '{actualToken}'");
                }

                continue;
            }

            if (assembly.Name.Version < oldVersionStart)
            {
                diagnostics.Add($"In {appConfigFileName}: {assembly.FullName} version is less than bindingRedirect range start {oldVersionStart}");
                continue;
            }

            if (assembly.Name.Version > oldVersionEnd)
            {
                diagnostics.Add($"In {appConfigFileName}: {assembly.FullName} version is higher than bindingRedirect range end {oldVersionEnd}");
                continue;
            }
        }

        if (versionMismatchesByName.TryGetValue(name, out var mismatches))
        {
            versionMismatchesByName.Remove(name);
            foreach (var versionMismatch in mismatches.ToArray())
            {
                var actualVersion = versionMismatch.ActualAssembly.Name.Version;
                if (actualVersion != newVersion)
                {
                    if (actualVersion < oldVersionStart)
                    {
                        diagnostics.Add($"In {appConfigFileName}: {versionMismatch.ActualAssembly.FullName} version is less than bindingRedirect range start {oldVersionStart} (Expected by {versionMismatch.Referencer.Name})");
                        continue;
                    }

                    if (actualVersion > oldVersionEnd)
                    {
                        diagnostics.Add($"In {appConfigFileName}: {versionMismatch.ActualAssembly.FullName} version is higher than bindingRedirect range end {oldVersionEnd} (Expected by {versionMismatch.Referencer.Name})");
                        continue;
                    }
                }
            }
        }

        if (!foundNewVersion)
        {
            var message = $"In {appConfigFileName}: couldn't find assembly '{name}' with version {newVersion}.";
            if (foundVersions.Count > 0)
            {
                message += $" Found versions: {string.Join(",", foundVersions.Select(v => v.ToString()).Distinct())}";
            }

            diagnostics.Add(message);
        }
    }
}