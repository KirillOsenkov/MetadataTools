using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public class Checker
    {
        Dictionary<AssemblyDefinition, HashSet<string>> assemblyToTypeList = new Dictionary<AssemblyDefinition, HashSet<string>>();
        StringBuilder sb = new StringBuilder();
        Dictionary<string, AssemblyDefinition> filePathToModuleDefinition = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;

        [STAThread]
        static void Main(string[] args)
        {
            var root = Environment.CurrentDirectory;
            var pattern = "*.dll";
            IEnumerable<string> files = Directory.GetFiles(root, pattern);
            pattern = "*.exe";
            files = files.Concat(Directory.GetFiles(root, pattern));
            new Checker().Check(files);
        }

        public void Check(IEnumerable<string> files)
        {
            this.files = files;

            foreach (var file in files)
            {
                var module = Load(file);
                if (module == null)
                {
                    continue;
                }

                var references = module.MainModule.AssemblyReferences;
                foreach (var reference in references)
                {
                    var resolved = Resolve(reference);
                    if (resolved == null)
                    {
                        Log($"Unable to resolve reference from {file} to {reference.FullName}");
                        continue;
                    }

                    Check(module, resolved);
                }
            }

            if (sb.Length > 0)
            {
                Clipboard.SetText(sb.ToString());
            }
        }

        public void Check(AssemblyDefinition referencing, AssemblyDefinition reference)
        {
            foreach (var referencedType in referencing.MainModule.GetTypeReferences())
            {
                if (referencedType.Scope.MetadataScopeType != MetadataScopeType.AssemblyNameReference || referencedType.Scope.Name != reference.Name.Name)
                {
                    continue;
                }

                var types = GetTypes(reference);
                if (!types.Contains(referencedType.FullName))
                {
                    Log($"Unable to find type {referencedType.FullName} needed by {referencing.Name} in {reference.Name}");
                }
            }
        }

        private HashSet<string> GetTypes(AssemblyDefinition assembly)
        {
            if (assemblyToTypeList.TryGetValue(assembly, out var types))
            {
                return types;
            }

            types = new HashSet<string>();
            assemblyToTypeList[assembly] = types;

            foreach (var topLevelType in assembly.MainModule.Types)
            {
                types.Add(topLevelType.FullName);
                AddNestedTypes(topLevelType, types);
            }

            foreach (var exportedType in assembly.MainModule.ExportedTypes)
            {
                types.Add(exportedType.FullName);
            }

            return types;
        }

        private void AddNestedTypes(TypeDefinition type, HashSet<string> types)
        {
            foreach (var nested in type.NestedTypes)
            {
                types.Add(nested.FullName);
                AddNestedTypes(nested, types);
            }
        }

        private void Log(string text)
        {
            Console.WriteLine(text);
            sb.AppendLine(text);
        }

        private AssemblyDefinition Resolve(AssemblyNameReference reference)
        {
            foreach (var assemblyDefinition in filePathToModuleDefinition)
            {
                if (assemblyDefinition.Value.Name.Name == reference.Name ||
                    string.Equals(Path.GetFileNameWithoutExtension(assemblyDefinition.Key), reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return assemblyDefinition.Value;
                }
            }

            foreach (var file in files)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), reference.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return Load(file);
                }
            }

            try
            {
                var assembly = Assembly.Load(reference.FullName);
                return Load(assembly.Location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private AssemblyDefinition Load(string filePath)
        {
            if (!filePathToModuleDefinition.TryGetValue(filePath, out var assemblyDefinition))
            {
                try
                {
                    assemblyDefinition = AssemblyDefinition.ReadAssembly(filePath);
                    filePathToModuleDefinition[filePath] = assemblyDefinition;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            return assemblyDefinition;
        }
    }
}
