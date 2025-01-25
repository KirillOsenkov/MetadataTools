using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace BinaryCompatChecker;

public partial class Checker
{
    private void CheckMembers(AssemblyDefinition assembly)
    {
        string assemblyFullName = assembly.Name.FullName;
        var module = assembly.MainModule;
        var references = module.GetTypeReferences().Concat(module.GetMemberReferences()).ToList();

        try
        {
            CheckTypeDefinitions(assemblyFullName, module, references);
        }
        catch
        {
        }

        HashSet<(AssemblyDefinition assemblyDefinition, string referenceName)> assembliesWithFailedMemberRefs = new();

        foreach (var memberReference in references)
        {
            try
            {
                var declaringType = memberReference.DeclaringType ?? (memberReference as TypeReference);
                if (declaringType != null && declaringType.IsArray)
                {
                    continue;
                }

                IMetadataScope scope = declaringType?.Scope;
                string referenceToAssembly = scope?.Name;

                if (referenceToAssembly != null && unresolvedAssemblies.Contains(referenceToAssembly))
                {
                    // already reported an unresolved assembly; just ignore this one
                    continue;
                }

                if (scope is AssemblyNameReference assemblyNameReference)
                {
                    referenceToAssembly = assemblyNameReference.FullName;
                }

                var resolved = memberReference.Resolve();
                if (resolved == null)
                {
                    bool report = memberReference is TypeReference ? commandLine.ReportMissingTypes : commandLine.ReportMissingMembers;
                    if (report)
                    {
                        string typeOrMember = memberReference is TypeReference ? "type" : "member";
                        diagnostics.Add($"In assembly '{assemblyFullName}': Failed to resolve {typeOrMember} reference '{memberReference.FullName}' in assembly '{referenceToAssembly}'");

                        var resolveKey = GetResolveKey(referenceToAssembly);
                        lock (resolveCache)
                        {
                            if (referenceToAssembly != null && resolveCache.TryGetValue(resolveKey, out var referencedAssemblyDefinition) && referencedAssemblyDefinition != null)
                            {
                                assembliesWithFailedMemberRefs.Add((referencedAssemblyDefinition, referenceToAssembly));
                            }
                        }
                    }
                }
                else
                {
                    var ivtUsage = TryGetIVTUsage(memberReference, resolved);
                    if (ivtUsage != null)
                    {
                        AddIVTUsage(ivtUsage);
                    }
                }
            }
            catch (AssemblyResolutionException resolutionException)
            {
                string unresolvedAssemblyName = resolutionException.AssemblyReference?.Name;
                if (unresolvedAssemblyName == null || unresolvedAssemblies.Add(unresolvedAssemblyName))
                {
                    diagnostics.Add($"In assembly '{assemblyFullName}': {resolutionException.Message}");
                }
            }
            catch (Exception ex)
            {
                bool report = true;

                TypeReference typeReference = memberReference as TypeReference ??
                    memberReference.DeclaringType;

                if (typeReference != null && typeReference.Scope?.Name is string scope && Framework.IsFrameworkName(scope))
                {
                    report = false;
                }

                if (report)
                {
                    diagnostics.Add($"In assembly '{assemblyFullName}': {ex.Message}");
                }
            }
        }

        foreach (var assemblyWithFailedMemberRefs in assembliesWithFailedMemberRefs)
        {
            var assemblyDefinition = assemblyWithFailedMemberRefs.assemblyDefinition;
            string relativePath = GetRelativePath(assemblyDefinition.MainModule.FileName);
            diagnostics.Add($"In assembly '{assemblyFullName}': reference '{assemblyWithFailedMemberRefs.referenceName}' resolved from '{relativePath}' as '{assemblyDefinition.FullName}'");
        }
    }
}