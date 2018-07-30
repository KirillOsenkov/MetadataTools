using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyBindingFixer
{
    public class FixerArguments
    {
        public string ApplicationPath { get; set; }
        public List<(string path, FrameworkDefinitionFileKind kind)> FrameworkDefinitions { get; } = new List<(string path, FrameworkDefinitionFileKind kind)>();
    }

    public class FrameworkDefinitionFile
    {
        public readonly string FilePath;
        public readonly IReadOnlyList<AssemblyNameReference> FrameworkAssemblies;

        public FrameworkDefinitionFile(string filePath, IReadOnlyList<AssemblyNameReference> frameworkAssemblies)
        {
            FilePath = Path.GetFullPath(filePath);
            FrameworkAssemblies = frameworkAssemblies;
        }
    }

    public enum FrameworkDefinitionFileKind
    {
        SimpleNameList,
        RedistList,
    }

    public enum DiagnosticLevel
    {
        Info,
        Error
    }

    public class FixResult
    {
        public List<(DiagnosticLevel level, string message)> Diagnostics { get; } = new List<(DiagnosticLevel, string)>();
    }
}
