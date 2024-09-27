using System;
using System.IO;
using GuiLabs.Metadata;

class Mvid
{
    static void Main(string[] args)
    {
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
