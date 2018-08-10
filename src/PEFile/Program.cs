using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using BenchmarkDotNet.Running;

namespace PEFile
{
    class Program
    {
        static void Main(string[] args)
        {
            //File.Copy(@"C:\temp\testrunner.exe", @"C:\temp\1.exe", true);
            //Corflags.Set32BitPreferred(@"C:\temp\1.exe", false);
            //return;
            //BenchmarkRunner.Run<MvidBenchmark>();
            //return;

            //var sw = Stopwatch.StartNew();
            //new MvidBenchmark().ReadUsingImageReader();
            //var elapsed = sw.Elapsed;
            //Console.WriteLine(elapsed);
            //return;

            if (args.Length != 1)
            {
                Console.WriteLine("Usage: mvid.exe <path-to-assembly>");
                return;
            }

            var filePath = args[0];
            if (!File.Exists(filePath))
            {
                Console.Error.WriteLine($"File {filePath} not found.");
                return;
            }

            filePath = Path.GetFullPath(filePath);
            Console.WriteLine(ImageReader.ReadAssemblyMvid(filePath));
        }
    }
}
