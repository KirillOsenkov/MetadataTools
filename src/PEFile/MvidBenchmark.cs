using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using BenchmarkDotNet.Attributes;

namespace GuiLabs.Metadata
{
    public class MvidBenchmark
    {
        private static string filePath = @"C:\Ide\bin\Debug\Microsoft.VisualStudio.ImageCatalog.dll";

        [Benchmark]
        public void ReadUsingReflection()
        {
            var assemblyName = AssemblyName.GetAssemblyName(filePath);
            var assembly = Assembly.Load(assemblyName);
            var mvid = assembly.ManifestModule.ModuleVersionId;
        }

        [Benchmark]
        public void ReadUsingSRM()
        {
            using (var stream = File.OpenRead(filePath))
            {
                PEReader reader = new PEReader(stream);
                var metadataReader = reader.GetMetadataReader();
                var mvidHandle = metadataReader.GetModuleDefinition().Mvid;
                var mvid = metadataReader.GetGuid(mvidHandle);
                // var timestamp = reader.PEHeaders.CoffHeader.TimeDateStamp;
            }
        }

        [Benchmark]
        public void ReadUsingCecil()
        {
            using (var module = Mono.Cecil.ModuleDefinition.ReadModule(filePath))
            {
                var mvid = module.Mvid;
            }
        }

        [Benchmark]
        public void ReadUsingImageReader()
        {
            var mvid = ImageReader.ReadAssemblyMvid(filePath);
        }
    }
}
