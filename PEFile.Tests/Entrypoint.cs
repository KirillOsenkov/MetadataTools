using BenchmarkDotNet.Running;

namespace PEFile.Tests
{
    class Entrypoint
    {
        static void Main(string[] args)
        {
            //BenchmarkRunner.Run<IsManagedAssemblyBenchmark>();
            // bool b = LargeAddressAware.IsLargeAddressAware(@"C:\temp\2.exe");
            //LargeAddressAware.SetLargeAddressAware(@"C:\temp\listbinaryinfo2.exe");
        }
    }
}
