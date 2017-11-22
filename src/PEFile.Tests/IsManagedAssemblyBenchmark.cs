using System;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Xunit;

namespace PEFile.Tests
{
    public class IsManagedAssemblyBenchmark
    {
        private static string filePath = @"C:\monodevelop\main\build\tests\git2-e8b8948.dll";

        [Fact]
        public void Run()
        {
            BenchmarkRunner.Run<IsManagedAssemblyBenchmark>();
        }

        [Benchmark]
        public void Reflection()
        {
            ReadAssemblyName(filePath);
        }

        [Benchmark]
        public void PEFileReader()
        {
            PEFile.PEFileReader.IsManagedAssembly(filePath);
        }

        private void ReadAssemblyName(string filePath)
        {
            try
            {
                var result = AssemblyName.GetAssemblyName(filePath);
            }
            catch (Exception)
            {
            }
        }
    }
}
