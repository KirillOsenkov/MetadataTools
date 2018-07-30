using BinaryCompatChecker;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static BinaryCompatChecker.AppConfigFile;

namespace AssemblyBindingFixer
{
    public class Fixer
    {
        private readonly FixerArguments Arguments;
        private readonly FixResult Result = new FixResult();

        private string ApplicationDirectory;
        private string AppConfigPath;
        private AssemblyDefinition ApplicationAssembly;
        private AppConfigFile AppConfigFile;

        private readonly HashSet<string> AttemptedAssemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssemblyDefinition> ResolvedAssemblies = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BindingRedirect> BindingRedirects = new Dictionary<string, BindingRedirect>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (AssemblyNameReference name, FrameworkDefinitionFile definitionFile)> FrameworkAssemblies = new Dictionary<string, (AssemblyNameReference, FrameworkDefinitionFile)>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<AssemblyDefinition> AssemblyQueue = new Queue<AssemblyDefinition>();
        private readonly HashSet<string> References = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Fixer(FixerArguments arguments)
        {
            Arguments = arguments;
        }

        public static FixResult Fix(FixerArguments arguments)
        {
            var fixer = new Fixer(arguments);
            fixer.Fix();
            return fixer.Result;
        }

        private void Fix()
        {
            try
            {
                Arguments.ApplicationPath = Path.GetFullPath(Arguments.ApplicationPath);
                ApplicationDirectory = Path.GetDirectoryName(Arguments.ApplicationPath);
                AppConfigPath = Arguments.ApplicationPath + ".config";
                FixCore();
            }
            catch (Exception ex)
            {
                Log(ex.ToString(), DiagnosticLevel.Error);
            }
        }

        private void AddAssembly(AssemblyDefinition assembly)
        {
            if (ResolvedAssemblies.ContainsKey(assembly.Name.Name))
            {
                return;
            }

            ResolvedAssemblies[assembly.Name.Name] = assembly;
            AssemblyQueue.Enqueue(assembly);
        }

        private void FixCore()
        {
            ApplicationAssembly = AssemblyDefinition.ReadAssembly(Arguments.ApplicationPath);
            AddAssembly(ApplicationAssembly);

            foreach (var frameworkDefinition in Arguments.FrameworkDefinitions)
            {
                var definitionFile = ReadFrameworkDefinition(frameworkDefinition);
                foreach (var frameworkAssembly in definitionFile.FrameworkAssemblies)
                {
                    FrameworkAssemblies[frameworkAssembly.Name] = (frameworkAssembly, definitionFile);
                }
            }

            if (File.Exists(AppConfigPath))
            {
                AppConfigFile = AppConfigFile.Read(AppConfigPath);

                foreach (var error in AppConfigFile.Errors)
                {
                    Log(error, DiagnosticLevel.Error);
                }

                foreach (var bindingRedirect in AppConfigFile.BindingRedirects)
                {
                    BindingRedirects[bindingRedirect.Name] = bindingRedirect;
                }
            }

            while (AssemblyQueue.Count != 0)
            {
                ProcessAssembly(AssemblyQueue.Dequeue());
            }

            AppConfigFile?.Write();
            File.WriteAllLines(Arguments.ApplicationPath + ".refs.txt", References.OrderBy(s => s));
        }

        private FrameworkDefinitionFile ReadFrameworkDefinition((string path, FrameworkDefinitionFileKind kind) frameworkDefinition)
        {
            List<AssemblyNameReference> frameworkAssemblies = new List<AssemblyNameReference>();

            try
            {
                switch (frameworkDefinition.kind)
                {
                    case FrameworkDefinitionFileKind.SimpleNameList:
                        ParseSimpleFrameworkList(frameworkDefinition.path, frameworkAssemblies);
                        break;
                    case FrameworkDefinitionFileKind.RedistList:
                        ParseFrameworkRedistList(frameworkDefinition.path, frameworkAssemblies);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"Exception encountered when reading framework definition file '{frameworkDefinition.path}': {ex}", DiagnosticLevel.Error);
            }

            return new FrameworkDefinitionFile(frameworkDefinition.path, frameworkAssemblies);
        }

        private void ParseFrameworkRedistList(string path, List<AssemblyNameReference> frameworkAssemblies)
        {
            var document = XDocument.Load(path);
            foreach (var fileElement in document.Root.Elements("File"))
            {
                try
                {
                    List<string> fullNameParts = new List<string>();
                    fullNameParts.Add(GetAttributeValue(fileElement, "AssemblyName", required: true));
                    parsePartAttribute("Version", required: true);
                    parsePartAttribute("Culture");
                    parsePartAttribute("PublicKeyToken");

                    void parsePartAttribute(string name, bool required = false)
                    {
                        var value = GetAttributeValue(fileElement, name, required);
                        if (value != null)
                        {
                            fullNameParts.Add($"{name}={value}");
                        }
                    }

                    frameworkAssemblies.Add(AssemblyNameReference.Parse(string.Join(", ", fullNameParts)));
                }
                catch (Exception ex)
                {
                    Log($"Unable to parse '{fileElement}' from '{path}': {ex}");
                }
            }
        }

        private static string GetAttributeValue(XElement element, string attributeName, bool required = false)
        {
            var value = element.Attribute(attributeName)?.Value;
            if (required && string.IsNullOrEmpty(value))
            {
                throw new InvalidDataException($"Missing required attribute: {attributeName}");
            }

            return value;
        }

        private void ParseSimpleFrameworkList(string path, List<AssemblyNameReference> frameworkAssemblies)
        {
            foreach (var fullName in File.ReadAllLines(path).Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                try
                {
                    frameworkAssemblies.Add(AssemblyNameReference.Parse(fullName));
                }
                catch (Exception ex)
                {
                    Log($"Unable to parse '{fullName}' from '{path}': {ex}");
                }
            }
        }

        private void ProcessAssembly(AssemblyDefinition assemblyDefinition)
        {
            foreach (var reference in assemblyDefinition.MainModule.AssemblyReferences)
            {
                References.Add(reference.ToString());
                Resolve(assemblyDefinition, reference);
            }
        }

        private void Resolve(AssemblyDefinition referencingAssembly, AssemblyNameReference reference)
        {
            var name = reference.Name;
            var publicKeyToken = BitConverter.ToString(reference.PublicKeyToken).Replace("-", "").ToLowerInvariant();

            if (!BindingRedirects.TryGetValue(name, out var bindingRedirect))
            {
                bindingRedirect = new BindingRedirect()
                {
                    OldVersionRangeStart = reference.Version,
                    OldVersionRangeEnd = reference.Version,
                    Culture = reference.Culture,
                    Name = reference.Name,
                    PublicKeyToken = publicKeyToken
                };

                BindingRedirects[name] = bindingRedirect;

                AppConfigFile?.AddBindingRedirect(bindingRedirect);
            }

            if (reference.Version < bindingRedirect.OldVersionRangeStart)
            {
                Log($"Extending binding redirect minimum version for {name} from {bindingRedirect.OldVersionRangeStart} to {reference.Version}");
                bindingRedirect.OldVersionRangeStart = reference.Version;
            }

            if (reference.Version > bindingRedirect.OldVersionRangeEnd)
            {
                Log($"Extending binding redirect maximum version for {name} from {bindingRedirect.OldVersionRangeEnd} to {reference.Version}");
                bindingRedirect.OldVersionRangeEnd = reference.Version;
            }

            if (TryResolve(referencingAssembly, reference, out var resolvedReference, out var resolvedPath))
            {
                if (bindingRedirect.NewVersion != resolvedReference.Version)
                {
                    Log($"Unifying to binding redirect version from {bindingRedirect.NewVersion} to found assembly version '{resolvedReference.Version}': {resolvedPath}");
                    bindingRedirect.NewVersion = resolvedReference.Version;
                }
            }
        }

        private bool TryResolve(AssemblyDefinition referencingAssembly, AssemblyNameReference reference, out AssemblyNameReference resolvedReference, out string path)
        {
            resolvedReference = null;
            path = null;

            var name = reference.Name;
            if (FrameworkAssemblies.TryGetValue(name, out var frameworkAssembly))
            {
                resolvedReference = frameworkAssembly.name;
                path = frameworkAssembly.definitionFile.FilePath;
                return true;
            }

            if (!ResolvedAssemblies.TryGetValue(reference.Name, out var resolvedReferenceAssembly))
            {
                if (!TryLoad(name, "dll", out resolvedReferenceAssembly) && !TryLoad(name, "exe", out resolvedReferenceAssembly))
                {
                    Log($"Could not load reference '{reference}' for '{referencingAssembly}'");
                }
                else
                {
                    AddAssembly(resolvedReferenceAssembly);
                }
            }

            if (resolvedReferenceAssembly == null)
            {
                return false;
            }
            else
            {
                resolvedReference = resolvedReferenceAssembly.Name;
                path = resolvedReferenceAssembly.MainModule.FileName;
                return true;
            }
        }

        private bool TryLoad(string assemblyName, string extension, out AssemblyDefinition assembly)
        {
            extension = extension.TrimStart('.');
            return TryLoad(Path.Combine(ApplicationDirectory, $"{assemblyName}.{extension}"), out assembly);
        }

        private bool TryLoad(string assemblyPath, out AssemblyDefinition assembly)
        {
            assembly = null;
            if (!AttemptedAssemblyPaths.Add(assemblyPath))
            {
                return false;
            }

            try
            {
                if (File.Exists(assemblyPath) && PEFile.IsManagedAssembly(assemblyPath))
                {
                    assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading assembly '{assemblyPath}': " + ex.ToString());
            }

            return false;
        }

        public void Log(string diagnostic, DiagnosticLevel level = DiagnosticLevel.Info)
        {
            Result.Diagnostics.Add((level, diagnostic));
        }
    }
}
