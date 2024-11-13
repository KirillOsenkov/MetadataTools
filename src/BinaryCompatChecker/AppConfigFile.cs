using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Mono.Cecil;

namespace BinaryCompatChecker
{
    public class AppConfigFile
    {
        private List<string> errors = new List<string>();
        public IEnumerable<string> Errors => errors;

        private List<BindingRedirect> bindingRedirects = new List<BindingRedirect>();
        public IEnumerable<BindingRedirect> BindingRedirects => bindingRedirects;

        private string filePath;
        private XDocument document;
        private XElement configurationElement = null;
        private XElement runtimeElement = null;
        private XElement firstAssemblyBindingElement = null;

        public string FilePath => filePath;
        public string FileName => Path.GetFileName(filePath);
        public string Directory => Path.GetDirectoryName(filePath);

        public bool HasCodeBases { get; set; }
        public bool IgnoreVersionMismatch { get; set; }

        private AppConfigFile(string filePath)
        {
            this.filePath = filePath;
        }

        public class BindingRedirect
        {
            public string Name { get; set; }
            public string Culture { get; set; }
            public string PublicKeyToken { get; set; }
            public Version OldVersionRangeStart { get; set; }
            public Version OldVersionRangeEnd { get; set; }
            public Version NewVersion { get; set; }
            public XElement AssemblyBindingElement { get; set; }
            public XElement DependentAssemblyElement { get; set; }
            public XElement AssemblyIdentityElement { get; set; }
            public XElement BindingRedirectElement { get; set; }
            public IReadOnlyList<CodeBase> CodeBases { get; set; } = Array.Empty<CodeBase>();

            public override string ToString()
            {
                return $"Name={Name} Culture={Culture} PublicKeyToken={PublicKeyToken} OldVersion={OldVersionRangeStart}-{OldVersionRangeEnd} NewVersion={NewVersion}";
            }

            public void AddOrUpdateElement()
            {
                if (NewVersion == null || ( OldVersionRangeStart == NewVersion && OldVersionRangeEnd == NewVersion))
                {
                    return;
                }

                if (NewVersion < OldVersionRangeStart)
                {
                    OldVersionRangeStart = NewVersion;
                }

                if (NewVersion > OldVersionRangeEnd)
                {
                    OldVersionRangeEnd = NewVersion;
                }

                if (DependentAssemblyElement == null)
                {
                    DependentAssemblyElement = new XElement(Xmlns("dependentAssembly"));
                    AssemblyBindingElement.Add(DependentAssemblyElement);
                }

                if (AssemblyIdentityElement == null)
                {
                    AssemblyIdentityElement = new XElement(Xmlns("assemblyIdentity"));
                    DependentAssemblyElement.Add(AssemblyIdentityElement);
                }

                AssemblyIdentityElement.SetAttributeValue("name", Name);
                if (!string.IsNullOrEmpty(Culture))
                {
                    AssemblyIdentityElement.SetAttributeValue("culture", Culture);
                }

                AssemblyIdentityElement.SetAttributeValue("publicKeyToken", PublicKeyToken);

                if (BindingRedirectElement == null)
                {
                    BindingRedirectElement = new XElement(Xmlns("bindingRedirect"));
                    DependentAssemblyElement.Add(BindingRedirectElement);
                }

                BindingRedirectElement.SetAttributeValue("oldVersion", $"{OldVersionRangeStart}-{OldVersionRangeEnd}");
                BindingRedirectElement.SetAttributeValue("newVersion", NewVersion);
            }
        }

        public class CodeBase
        {
            public Version Version { get; set; }
            public string Href { get; set; }
            public XElement CodeBaseElement { get; set; }
            public string FilePath { get; set; }
            public AssemblyDefinition AssemblyDefinition { get; set; }

            public override string ToString()
            {
                return $"version={Version} href={Href}";
            }
        }

        public static AppConfigFile Read(string filePath)
        {
            var appConfigFile = new AppConfigFile(filePath);
            try
            {
                appConfigFile.Parse(filePath);
            }
            catch (Exception ex)
            {
                appConfigFile.errors.Add(ex.Message);
            }

            return appConfigFile;
        }

        public static void ReplicateBindingRedirects(string sourceFilePath, IEnumerable<string> destinationFilePaths)
        {
            var sourceAppConfig = Read(sourceFilePath);
            foreach (var destination in destinationFilePaths)
            {
                ReplicateBindingRedirects(sourceAppConfig, destination);
            }
        }

        private static void ReplicateBindingRedirects(AppConfigFile appConfig, string destinationFilePath)
        {
            var destination = Read(destinationFilePath);

            foreach (var bindingRedirect in appConfig.BindingRedirects)
            {
                // need to clone to ensure new XElements are created in the new tree
                var newBindingRedirect = new BindingRedirect
                {
                    Name = bindingRedirect.Name,
                    Culture = bindingRedirect.Culture,
                    PublicKeyToken = bindingRedirect.PublicKeyToken,
                    OldVersionRangeStart = bindingRedirect.OldVersionRangeStart,
                    OldVersionRangeEnd = bindingRedirect.OldVersionRangeEnd,
                    NewVersion = bindingRedirect.NewVersion,
                    CodeBases = bindingRedirect.CodeBases.Select(c => new CodeBase { Version = c.Version, Href = c.Href }).ToArray()
                };
                destination.AddBindingRedirect(newBindingRedirect);
            }

            destination.Write();
        }

        public void Write()
        {
            if (runtimeElement == null)
            {
                runtimeElement = new XElement("runtime");
                configurationElement.Add(runtimeElement);
            }

            if (firstAssemblyBindingElement == null)
            {
                firstAssemblyBindingElement = new XElement(Xmlns("assemblyBinding"));
                runtimeElement.Add(firstAssemblyBindingElement);
            }

            foreach (var bindingRedirect in bindingRedirects)
            {
                if (bindingRedirect.AssemblyBindingElement == null)
                {
                    bindingRedirect.AssemblyBindingElement = firstAssemblyBindingElement;
                }

                bindingRedirect.AddOrUpdateElement();
            }

            Save();
        }

        private void Save()
        {
            var originalBytes = File.ReadAllBytes(filePath);
            byte[] newBytes = null;

            var xws = new XmlWriterSettings
            {
                Indent = true
            };
            using (var memoryStream = new MemoryStream())
            using (var xmlWriter = XmlWriter.Create(memoryStream, xws))
            {
                document.Save(xmlWriter);
                xmlWriter.Flush();
                newBytes = memoryStream.ToArray();
            }

            if (!Enumerable.SequenceEqual(originalBytes, newBytes))
            {
                Console.WriteLine($"Writing {filePath}");
                File.WriteAllBytes(filePath, newBytes);
            }
        }

        public void AddBindingRedirect(BindingRedirect bindingRedirect)
        {
            var existing = bindingRedirects.FirstOrDefault(b => b.Name.Equals(bindingRedirect.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Name = bindingRedirect.Name;
                existing.Culture = bindingRedirect.Culture;
                existing.PublicKeyToken = bindingRedirect.PublicKeyToken;
                existing.OldVersionRangeStart = bindingRedirect.OldVersionRangeStart;

                // if the existing version is something like 100.0.0.0, don't touch it
                if (bindingRedirect.OldVersionRangeEnd == null ||
                    existing.OldVersionRangeEnd == null ||
                    bindingRedirect.OldVersionRangeEnd > existing.OldVersionRangeEnd)
                {
                    existing.OldVersionRangeEnd = bindingRedirect.OldVersionRangeEnd;
                }

                existing.NewVersion = bindingRedirect.NewVersion;
            }
            else
            {
                bindingRedirects.Add(bindingRedirect);
            }
        }

        private static XName Xmlns(string shortName) => XName.Get(shortName, "urn:schemas-microsoft-com:asm.v1");

        private void Parse(string appConfigFilePath)
        {
            void Error(string text) => errors.Add(text);

            document = XDocument.Load(appConfigFilePath);
            configurationElement = document.Root;
            runtimeElement = configurationElement.Element("runtime");
            if (runtimeElement == null)
            {
                return;
            }

            var assemblyBindingElements = runtimeElement.Elements(Xmlns("assemblyBinding"));
            if (assemblyBindingElements == null || !assemblyBindingElements.Any())
            {
                return;
            }

            firstAssemblyBindingElement = assemblyBindingElements.FirstOrDefault();

            var dependentAssemblyElements = assemblyBindingElements.Elements(Xmlns("dependentAssembly"));
            foreach (var dependentAssembly in dependentAssemblyElements)
            {
                var assemblyIdentity = dependentAssembly.Element(Xmlns("assemblyIdentity"));
                if (assemblyIdentity == null)
                {
                    Error($"One of dependentAssembly elements doesn't have an assemblyIdentity subelement");
                    continue;
                }

                var name = GetAttributeValue(assemblyIdentity, "name");
                if (name == null)
                {
                    Error($"assemblyIdentity is missing the 'name' attribute");
                    continue;
                }

                var culture = GetAttributeValue(assemblyIdentity, "culture");

                var publicKeyToken = GetAttributeValue(assemblyIdentity, "publicKeyToken");
                if (publicKeyToken == null)
                {
                    Error($"assemblyIdentity {name} is missing the 'publicKeyToken' attribute");
                    publicKeyToken = "<missing>";
                }

                var bindingRedirect = dependentAssembly.Element(Xmlns("bindingRedirect"));
                var codeBases = dependentAssembly.Elements(Xmlns("codeBase"));

                if (bindingRedirect == null && (codeBases == null || !codeBases.Any()))
                {
                    Error($"dependentAssembly for {name} doesn't have a bindingRedirect or codeBase subelements");
                    continue;
                }

                Version oldVersionStart = null;
                Version oldVersionEnd = null;
                Version newVersion = null;

                if (bindingRedirect != null)
                {
                    var oldVersionString = GetAttributeValue(bindingRedirect, "oldVersion");
                    if (oldVersionString == null)
                    {
                        Error($"bindingRedirect for {name} is missing the 'oldVersion' attribute");
                        continue;
                    }

                    var newVersionString = GetAttributeValue(bindingRedirect, "newVersion");
                    if (newVersionString == null)
                    {
                        Error($"bindingRedirect for {name} is missing the 'newVersion' attribute");
                        continue;
                    }

                    Tuple<string, string> range = ParseVersionRange(oldVersionString);
                    if (range == null)
                    {
                        Error($"oldVersion range for {name} is in incorrect format");
                        continue;
                    }

                    if (!Version.TryParse(range.Item1, out oldVersionStart))
                    {
                        Error($"Can't parse old start version: {range.Item1}");
                        continue;
                    }

                    if (!Version.TryParse(range.Item2, out oldVersionEnd))
                    {
                        Error($"Can't parse old end version: {range.Item2}");
                        continue;
                    }

                    if (!Version.TryParse(newVersionString, out newVersion))
                    {
                        Error($"Can't parse newVersion: {newVersion}");
                        continue;
                    }
                }

                var codeBaseList = new List<CodeBase>();
                if (codeBases != null && codeBases.Any())
                {
                    foreach (var codeBase in codeBases)
                    {
                        var versionString = GetAttributeValue(codeBase, "version");
                        if (versionString == null)
                        {
                            Error($"codeBase for {name}: 'version' attribute missing");
                            continue;
                        }

                        var hrefString = GetAttributeValue(codeBase, "href");
                        if (hrefString == null)
                        {
                            Error($"codeBase for {name}: 'href' attribute missing");
                            continue;
                        }

                        if (!Version.TryParse(versionString, out var version))
                        {
                            Error($"codeBase for {name}: invalid version: {versionString}");
                            continue;
                        }

                        string filePath = Path.Combine(Directory, hrefString);
                        if (!File.Exists(filePath))
                        {
                            Error($"codeBase for {name}: 'href' file not found: {hrefString}");
                            continue;
                        }

                        var newCodeBase = new CodeBase()
                        {
                            Version = version,
                            Href = hrefString,
                            CodeBaseElement = codeBase,
                            FilePath = filePath
                        };
                        codeBaseList.Add(newCodeBase);
                        HasCodeBases = true;
                    }
                }

                var bindingRedirectResult = new BindingRedirect
                {
                    Name = name,
                    Culture = culture,
                    PublicKeyToken = publicKeyToken,
                    OldVersionRangeStart = oldVersionStart,
                    OldVersionRangeEnd = oldVersionEnd,
                    NewVersion = newVersion,
                    AssemblyBindingElement = dependentAssembly.Parent,
                    DependentAssemblyElement = dependentAssembly,
                    BindingRedirectElement = bindingRedirect,
                    AssemblyIdentityElement = assemblyIdentity,
                    CodeBases = codeBaseList
                };

                if (bindingRedirects.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    Error($"Duplicate binding redirect: {name}");
                    continue;
                }

                bindingRedirects.Add(bindingRedirectResult);
            }
        }

        private Tuple<string, string> ParseVersionRange(string versionRange)
        {
            int dash = versionRange.IndexOf('-');
            if (dash <= 0 || dash == versionRange.Length - 1)
            {
                return null;
            }

            string first = versionRange.Substring(0, dash);
            string second = versionRange.Substring(dash + 1, versionRange.Length - dash - 1);
            return Tuple.Create(first, second);
        }

        private string GetAttributeValue(XElement element, string attributeName)
        {
            return element.Attribute(attributeName)?.Value;
        }
    }
}
