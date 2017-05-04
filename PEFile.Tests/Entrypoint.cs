using BenchmarkDotNet.Running;

namespace PEFile.Tests
{
    class Entrypoint
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<IsManagedAssemblyBenchmark>();
        }
    }
}
