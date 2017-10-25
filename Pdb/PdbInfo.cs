using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PEFile
{
    public class PdbInfo
    {
        public string SymbolsPath => "http://<path-to-symbols>";

        public static Action<string> LogAction;

        public string AssemblyFilePath { get; set; }
        public string AssemblyShortName => System.IO.Path.GetFileNameWithoutExtension(AssemblyFilePath).ToLowerInvariant();
        public Guid Guid { get; set; }
        public int Age { get; set; }
        public string Path { get; set; }
        public string SymbolsUrl => $"{SymbolsPath}/{AssemblyShortName}.pdb/{Guid.ToString("N").ToUpperInvariant()}{Age}/file.ptr";

        public static void Log(string text)
        {
            LogAction?.Invoke(text);
        }

        public override string ToString()
        {
            return $"{Guid.ToString("D")} {Age} {Path}";
        }

        public static IEnumerable<PdbInfo> Read(string assemblyFilePath)
        {
            try
            {
                var list = ReadList(assemblyFilePath).ToArray();
                return list;
            }
            catch
            {
                return null;
            }
        }

        public static bool IsMatch(string assemblyFilePath, string pdbFilePath)
        {
            var list = Read(assemblyFilePath);
            var pdbGuid = TryReadPdbGuid(pdbFilePath);
            if (pdbGuid != Guid.Empty)
            {
                foreach (var debugDirectoryEntry in list)
                {
                    if (debugDirectoryEntry.Guid == pdbGuid)
                    {
                        Log("Guid match: " + pdbGuid.ToString());
                        return true;
                    }
                }

                return false;
            }

            return IsMatchWindowsPdb(assemblyFilePath, pdbFilePath);
        }

        private static bool IsMatchWindowsPdb(string assemblyFilePath, string pdbFilePath)
        {
            throw new NotImplementedException();
        }

        private static IEnumerable<PdbInfo> ReadList(string assemblyFilePath)
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
                        var info = new PdbInfo
                        {
                            AssemblyFilePath = assemblyFilePath,
                            Guid = codeViewDebugDirectoryData.Guid,
                            Age = codeViewDebugDirectoryData.Age,
                            Path = codeViewDebugDirectoryData.Path
                        };
                        Log(info.ToString());
                        yield return info;
                    }
                }
            }
        }

        public static Guid TryReadPdbGuid(string pdbFilePath)
        {
            try
            {
                using (var stream = File.OpenRead(pdbFilePath))
                {
                    if (stream.Length < 1024)
                    {
                        return Guid.Empty;
                    }

                    if (stream.ReadByte() != 'B' ||
                        stream.ReadByte() != 'S' ||
                        stream.ReadByte() != 'J' ||
                        stream.ReadByte() != 'B')
                    {
                        // not a portable Pdb
                        Log("Not a portable Pdb");
                        return Guid.Empty;
                    }

                    Log("Portable Pdb");

                    stream.Position = 0;

                    using (var provider = MetadataReaderProvider.FromPortablePdbStream(stream))
                    {
                        var metadataReader = provider.GetMetadataReader();
                        var id = metadataReader.DebugMetadataHeader.Id;
                        var guid = new Guid(id.Take(16).ToArray());
                        var stamp = id.Skip(16).ToArray();
                        Log("Portable Pdb Guid: " + guid.ToString("D"));
                        return guid;
                    }
                }
            }
            catch (Exception)
            {
                return Guid.Empty;
            }
        }

        private static Guid TryReadWindowsPdbGuid(string pdbFilePath)
        {
            throw new NotImplementedException();
        }

        public static void DownloadPdb(string assemblyFilePath)
        {
            var pdbInfo = Read(assemblyFilePath);
            foreach (var item in pdbInfo)
            {
                DownloadPdb(item); 
            }
        }

        public static void DownloadPdb(PdbInfo pdbInfo)
        {
            var client = new HttpClient();
            var text = client.GetStringAsync(pdbInfo.SymbolsUrl).Result;
            if (text.StartsWith("PATH:"))
            {
                text = text.Substring(5);
            }

            //File.Copy(text, @"C:\Temp\1.pdb", overwrite: true);
        }
    }
}
