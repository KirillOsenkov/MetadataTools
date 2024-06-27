using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using BenchmarkDotNet.Running;

namespace GuiLabs.Metadata
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

            if (args.Length == 0)
            {
                var files = Directory.GetFiles(Environment.CurrentDirectory, "*.dll", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    Process(file);
                }
            }
            else if (args.Length == 1)
            {
                var filePath = args[0];
                if (!File.Exists(filePath))
                {
                    if (filePath.Contains("*") || filePath.Contains("?"))
                    {
                        var files = Directory.GetFiles(Environment.CurrentDirectory, filePath, SearchOption.AllDirectories);
                        foreach (var file in files)
                        {
                            Process(file);
                        }

                        return;
                    }

                    Console.Error.WriteLine($"File {filePath} not found.");
                    return;
                }
                else
                {
                    Process(filePath);
                }
            }
            else
            {
                Console.WriteLine("Usage: mvid.exe [<path-to-assembly>]");
                return;
            }
        }

        private static void Process(string filePath)
        {
            Console.WriteLine(filePath);
            Console.WriteLine(ImageReader.ReadAssemblyMvid(filePath));
        }
    }
}
