﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.DiaSymReader;
using Mono.Cecil.Cil;

namespace MetadataTools
{
    public class ModuleInfo
    {
        public string FilePath { get; set; }
        public string SourceLink { get; set; }
        public IReadOnlyList<PdbRecord> PdbEntries { get; set; }
        public IList<PdbChecksum> PdbChecksums { get; set; }
        public bool HasEmbeddedPdb { get; set; }
        public bool Reproducible { get; set; }

        public static ModuleInfo Read(string assemblyFilePath)
        {
            var moduleInfo = new ModuleInfo();
            moduleInfo.FilePath = assemblyFilePath;

            var list = new List<PdbRecord>();
            moduleInfo.PdbEntries = list;

            moduleInfo.PdbChecksums = new List<PdbChecksum>();

            try
            {
                using (var stream = File.OpenRead(assemblyFilePath))
                {
                    PEReader reader = new PEReader(stream);
                    var metadataReader = reader.GetMetadataReader();

                    var debugDirectory = reader.ReadDebugDirectory();
                    foreach (var entry in debugDirectory)
                    {
                        if (entry.Type == DebugDirectoryEntryType.CodeView)
                        {
                            var data = reader.ReadCodeViewDebugDirectoryData(entry);
                            var pdbEntry = new PdbRecord
                            {
                                AssemblyFilePath = assemblyFilePath,
                                Guid = data.Guid,
                                Age = data.Age,
                                Path = data.Path,
                                Stamp = entry.Stamp
                            };
                            list.Add(pdbEntry);
                        }
                        else if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                        {
                            moduleInfo.HasEmbeddedPdb = true;
                        }
                        else if (entry.Type == DebugDirectoryEntryType.Reproducible)
                        {
                            moduleInfo.Reproducible = true;
                        }
                        else if (entry.Type == DebugDirectoryEntryType.PdbChecksum)
                        {
                            var data = reader.ReadPdbChecksumDebugDirectoryData(entry);
                            var checksum = new PdbChecksum
                            {
                                Algorithm = data.AlgorithmName,
                                Checksum = Convert.ToHexString(data.Checksum.ToArray())
                            };
                            moduleInfo.PdbChecksums.Add(checksum);
                        }
                    }
                }

                if (moduleInfo.HasEmbeddedPdb)
                {
                    moduleInfo.SourceLink = ReadSourceLinkFromEmbeddedPdb(assemblyFilePath);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading file: {ex.Message}");
            }

            return moduleInfo;
        }

        public static string ReadSourceLinkFromEmbeddedPdb(string assemblyFilePath)
        {
            var readerParameters = new Mono.Cecil.ReaderParameters
            {
                ReadSymbols = true,
                SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false) { },
                ThrowIfSymbolsAreNotMatching = false,
                ReadingMode = Mono.Cecil.ReadingMode.Deferred
            };
            using var module = Mono.Cecil.ModuleDefinition.ReadModule(assemblyFilePath, readerParameters);
            if (module.HasCustomDebugInformations)
            {
                foreach (var custom in module.CustomDebugInformations)
                {
                    if (custom is not SourceLinkDebugInformation sourceLinkDebugInformation)
                    {
                        continue;
                    }

                    var sourceLink = sourceLinkDebugInformation.Content;
                    if (!string.IsNullOrWhiteSpace(sourceLink))
                    {
                        return sourceLink;
                    }
                }
            }

            return null;
        }
    }

    public class PdbInfo
    {
        public static bool IsMatch(string assemblyFilePath, string pdbFilePath)
        {
            var moduleInfo = ModuleInfo.Read(assemblyFilePath);
            return IsMatch(moduleInfo, pdbFilePath);
        }

        public static bool IsMatchWindowsPdb(IEnumerable<PdbRecord> debugDirectory, string pdbFilePath)
        {
            using (var pdbStream = File.OpenRead(pdbFilePath))
            {
                var metadataProvider = new SymReaderMetadataProvider();
                var reader = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(pdbStream, metadataProvider);
                foreach (var item in debugDirectory)
                {
                    reader.MatchesModule(item.Guid, item.Stamp, item.Age, out var isMatch);
                    if (isMatch)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool IsMatch(ModuleInfo moduleInfo, string pdbFilePath)
        {
            var pdb = TryReadPortablePdb(pdbFilePath);
            if (pdb != null)
            {
                foreach (var debugDirectoryEntry in moduleInfo.PdbEntries)
                {
                    if (debugDirectoryEntry.Guid == pdb.Guid)
                    {
                        return true;
                    }
                }

                return false;
            }

            return IsMatchWindowsPdb(moduleInfo.PdbEntries, pdbFilePath);
        }

        public static PortablePdb TryReadPortablePdb(string pdbFilePath)
        {
            try
            {
                using (var stream = File.OpenRead(pdbFilePath))
                {
                    if (stream.Length < 1024)
                    {
                        return null;
                    }

                    if (stream.ReadByte() != 'B' ||
                        stream.ReadByte() != 'S' ||
                        stream.ReadByte() != 'J' ||
                        stream.ReadByte() != 'B')
                    {
                        // not a portable Pdb
                        return null;
                    }

                    stream.Position = 0;

                    using (var provider = MetadataReaderProvider.FromPortablePdbStream(stream))
                    {
                        var metadataReader = provider.GetMetadataReader();

                        var id = metadataReader.DebugMetadataHeader.Id;
                        var guid = new Guid(id.Take(16).ToArray());
                        var stamp = id.Skip(16).ToArray();

                        var pdb = new PortablePdb();
                        pdb.Guid = guid;
                        pdb.Stamp = stamp;
                        pdb.SourceLink = ReadSourceLinkFromPortablePdb(metadataReader);

                        return pdb;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string ReadSourceServerDataFromNativePdb(string pdbFilePath)
        {
            var bytes = File.ReadAllBytes(pdbFilePath);
            var stream = new MemoryStream(bytes);
            var reader5 = SymUnmanagedReaderFactory.CreateReader<ISymUnmanagedReader5>(stream, new SymReaderMetadataProvider());
            var data = PdbSrcSvr.GetSourceServerData(reader5);
            return data;
        }

        public static readonly Guid SourceLinkGuid = new Guid("{CC110556-A091-4D38-9FEC-25AB9A351A6A}");

        public static string ReadSourceLinkFromPortablePdb(MetadataReader metadataReader)
        {
            foreach (var customDebugInfoHandle in metadataReader.CustomDebugInformation)
            {
                var customDebugInformation = metadataReader.GetCustomDebugInformation(customDebugInfoHandle);
                var guid = metadataReader.GetGuid(customDebugInformation.Kind);
                if (guid == SourceLinkGuid)
                {
                    var bytes = metadataReader.GetBlobBytes(customDebugInformation.Value);
                    var text = new StreamReader(new MemoryStream(bytes)).ReadToEnd();
                    return text;
                }
            }

            return null;
        }
    }

    public class PortablePdb
    {
        public Guid Guid { get; internal set; }
        public byte[] Stamp { get; internal set; }
        public string SourceLink { get; internal set; }
    }

    public class PdbChecksum
    {
        public string Algorithm { get; internal set; }
        public string Checksum { get; internal set; }
    }

    public class PdbRecord
    {
        public static Action<string> LogAction;

        public string AssemblyFilePath { get; set; }
        public string AssemblyShortName => System.IO.Path.GetFileNameWithoutExtension(AssemblyFilePath).ToLowerInvariant();

        public Guid Guid { get; set; }
        public uint Stamp { get; set; }
        public int Age { get; set; }
        public string Path { get; set; }

        public PdbRecord()
        {
        }

        public static void Log(string text)
        {
            LogAction?.Invoke(text);
        }

        public override string ToString()
        {
            return $"{Guid:D} {Age} {Path} {Guid.ToString("N").ToUpperInvariant()}{Age}";
        }

        public string DownloadPdb(string serverUrl)
        {
            var client = new HttpClient();
            var url = $"{serverUrl}/{AssemblyShortName}.pdb/{Guid.ToString("N").ToUpperInvariant()}{Age}/file.ptr";
            string text;
            try
            {
                Console.WriteLine($"Downloading {url}");
                text = client.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log("Error: " + ex.Message);
                return null;
            }

            if (text.StartsWith("PATH:"))
            {
                text = text.Substring(5);
                if (File.Exists(text))
                {
                    Console.WriteLine(text);

                    var destinationPdb = System.IO.Path.ChangeExtension(AssemblyFilePath, ".pdb");
                    if (!File.Exists(destinationPdb))
                    {
                        try
                        {
                            File.Copy(text, destinationPdb);
                        }
                        catch (SystemException exception) when (exception is UnauthorizedAccessException || exception is AccessViolationException)
                        {
                            Log($"Couldn't write to {destinationPdb}, trying the Downloads folder.");
                            destinationPdb = System.IO.Path.GetFileNameWithoutExtension(AssemblyFilePath) + ".pdb";
                            destinationPdb = System.IO.Path.Combine(Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\Downloads"), destinationPdb);
                            try
                            {
                                File.Copy(text, destinationPdb);
                            }
                            catch (Exception ex)
                            {
                                Log(ex.ToString());
                                return null;
                            }
                        }

                        Log($"Downloaded {destinationPdb}");
                        return destinationPdb;
                    }
                    else
                    {
                        Log($"{destinationPdb} already exists.");
                        return destinationPdb;
                    }
                }
                else
                {
                    Console.Error.WriteLine($"File doesn't exist: {text}");
                }
            }
            else
            {
                Console.Error.WriteLine(text);
            }

            return null;
        }
    }
}
