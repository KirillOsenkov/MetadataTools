using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PEFile
{
    public class PdbInfo
    {
        public string SymbolsPath => "http://<path-to-symbols>";

        public string AssemblyFilePath { get; set; }
        public string AssemblyShortName => System.IO.Path.GetFileNameWithoutExtension(AssemblyFilePath).ToLowerInvariant();
        public Guid Guid { get; set; }
        public int Age { get; set; }
        public string Path { get; set; }
        public string SymbolsUrl => $"{SymbolsPath}/{AssemblyShortName}.pdb/{Guid.ToString("N").ToUpperInvariant()}{Age}/file.ptr";

        public override string ToString()
        {
            return $"{Guid.ToString("D")} {Age} {Path}";
        }

        public static PdbInfo Read(string assemblyFilePath)
        {
            try
            {
                using (var stream = File.OpenRead(assemblyFilePath))
                {
                    PEReader reader = new PEReader(stream);
                    var metadataReader = reader.GetMetadataReader();
                    var debugDirectory = reader.ReadDebugDirectory();
                    foreach (var debugDirectoryEntry in debugDirectory)
                    {
                        if (debugDirectoryEntry.Type == DebugDirectoryEntryType.CodeView)
                        {
                            var codeViewDebugDirectoryData = reader.ReadCodeViewDebugDirectoryData(debugDirectoryEntry);
                            return new PdbInfo
                            {
                                AssemblyFilePath = assemblyFilePath,
                                Guid = codeViewDebugDirectoryData.Guid,
                                Age = codeViewDebugDirectoryData.Age,
                                Path = codeViewDebugDirectoryData.Path
                            };
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }
    }
}
