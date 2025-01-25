using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace BinaryCompatChecker;

public class AssemblyCache
{
    private readonly Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new(CommandLine.PathComparer);

    public static AssemblyCache Instance { get; } = new AssemblyCache();

    public string TryResolve(AssemblyNameReference reference, bool strictVersion)
    {
        lock (filePathToModuleDefinition)
        {
            foreach (var assemblyDefinition in filePathToModuleDefinition)
            {
                if (assemblyDefinition.Value == null)
                {
                    continue;
                }

                if (string.Equals(assemblyDefinition.Value.Name.FullName, reference.FullName, StringComparison.OrdinalIgnoreCase) ||
                    (!strictVersion && string.Equals(Path.GetFileNameWithoutExtension(assemblyDefinition.Key), reference.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    string filePath = assemblyDefinition.Value.MainModule.FileName;
                    return filePath;
                }
            }
        }

        return null;
    }

    public (AssemblyDefinition assemblyDefinition, bool fromCache) Load(string filePath, IAssemblyResolver resolver, HashSet<string> diagnostics)
    {
        bool fromCache = true;

        lock (filePathToModuleDefinition)
        {
            if (!filePathToModuleDefinition.TryGetValue(filePath, out var assemblyDefinition))
            {
                fromCache = false;

                try
                {
                    if (!GuiLabs.Metadata.PEFile.IsManagedAssembly(filePath))
                    {
                        filePathToModuleDefinition[filePath] = null;
                        return (null, fromCache);
                    }

                    var readerParameters = new ReaderParameters
                    {
                        AssemblyResolver = resolver,
                        InMemory = true
                    };
                    assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath, readerParameters);
                    filePathToModuleDefinition[filePath] = assemblyDefinition;
                }
                catch (Exception ex)
                {
                    lock (diagnostics)
                    {
                        diagnostics.Add(ex.ToString());
                    }

                    return (null, fromCache);
                }
            }

            return (assemblyDefinition, fromCache);
        }
    }
}