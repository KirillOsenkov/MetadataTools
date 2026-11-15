using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace FindUnusedMembers
{
    public class Resolver : DefaultAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> targets = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssemblyDefinition> bySimpleName = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> searchDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Resolver()
        {
            RemoveSearchDirectory(".");
            RemoveSearchDirectory("bin");

            var windows = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(windows))
            {
                var framework = Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319");
                if (!Directory.Exists(framework))
                {
                    framework = Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319");
                }

                AddSearchDirectoryOnce(framework);
                AddSearchDirectoryOnce(Path.Combine(framework, "WPF"));
            }
        }

        public void RegisterTarget(AssemblyDefinition assembly)
        {
            targets[assembly.Name.Name] = assembly;
            AddSearchDirectoryOnce(Path.GetDirectoryName(assembly.MainModule.FileName));
        }

        public new void AddSearchDirectory(string directory)
        {
            AddSearchDirectoryOnce(directory);
        }

        private void AddSearchDirectoryOnce(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            if (searchDirectories.Add(directory))
            {
                base.AddSearchDirectory(directory);
            }
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return Resolve(name, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred });
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (targets.TryGetValue(name.Name, out var target))
            {
                return target;
            }

            if (bySimpleName.TryGetValue(name.Name, out var cached))
            {
                return cached;
            }

            if (failed.Contains(name.Name))
            {
                throw new AssemblyResolutionException(name);
            }

            parameters ??= new ReaderParameters();
            parameters.AssemblyResolver = this;
            parameters.ReadingMode = ReadingMode.Deferred;

            try
            {
                var assembly = base.Resolve(name, parameters);
                bySimpleName[name.Name] = assembly;
                return assembly;
            }
            catch (AssemblyResolutionException)
            {
                var relaxed = new AssemblyNameReference(name.Name, null);
                try
                {
                    var assembly = base.Resolve(relaxed, parameters);
                    bySimpleName[name.Name] = assembly;
                    return assembly;
                }
                catch (AssemblyResolutionException)
                {
                    failed.Add(name.Name);
                    throw;
                }
            }
        }
    }
}
