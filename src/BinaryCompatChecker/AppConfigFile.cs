using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

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
        private XElement assemblyBindingElement = null;

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
            public XElement DependentAssemblyElement { get; set; }
            public XElement AssemblyIdentityElement { get; set; }
            public XElement BindingRedirectElement { get; set; }

            public override string ToString()
            {
                return $"Name={Name} Culture={Culture} PublicKeyToken={PublicKeyToken} OldVersion={OldVersionRangeStart}-{OldVersionRangeEnd} NewVersion={NewVersion}";
            }

            public void AddOrUpdateElement(XElement parent)
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
                    parent.Add(DependentAssemblyElement);
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

        public void Write()
        {
            foreach (var bindingRedirect in bindingRedirects)
            {
                bindingRedirect.AddOrUpdateElement(assemblyBindingElement);
            }

            document.Save(filePath);
        }

        public void AddBindingRedirect(BindingRedirect bindingRedirect)
        {
            bindingRedirects.Add(bindingRedirect);
        }

        private static XName Xmlns(string shortName) => XName.Get(shortName, "urn:schemas-microsoft-com:asm.v1");

        private void Parse(string appConfigFilePath)
        {
            void Error(string text) => errors.Add(text);

            document = XDocument.Load(appConfigFilePath);
            var configuration = document.Root;
            var runtime = configuration.Element("runtime");
            if (runtime == null)
            {
                Error($"Element 'runtime' not found");
                return;
            }

            var assemblyBinding = runtime.Elements(Xmlns("assemblyBinding"));
            if (assemblyBinding == null || !assemblyBinding.Any())
            {
                Error($"Element 'assemblyBinding' not found");
                return;
            }

            assemblyBindingElement = assemblyBinding.FirstOrDefault();

            var dependentAssemblyElements = assemblyBinding.Elements(Xmlns("dependentAssembly"));
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
                if (bindingRedirect == null)
                {
                    Error($"dependentAssembly for {name} doesn't have a bindingRedirect subelement");
                    continue;
                }

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

                if (!Version.TryParse(range.Item1, out var oldVersionStart))
                {
                    Error($"Can't parse old start version: {range.Item1}");
                    continue;
                }

                if (!Version.TryParse(range.Item2, out var oldVersionEnd))
                {
                    Error($"Can't parse old end version: {range.Item2}");
                    continue;
                }

                if (!Version.TryParse(newVersionString, out var newVersion))
                {
                    Error($"Can't parse newVersion: {newVersion}");
                    continue;
                }

                var bindingRedirectResult = new BindingRedirect
                {
                    Name = name,
                    Culture = culture,
                    PublicKeyToken = publicKeyToken,
                    OldVersionRangeStart = oldVersionStart,
                    OldVersionRangeEnd = oldVersionEnd,
                    NewVersion = newVersion,
                    DependentAssemblyElement = dependentAssembly,
                    BindingRedirectElement = bindingRedirect,
                    AssemblyIdentityElement = assemblyIdentity
                };

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
