using System;
using System.IO;
using Mono.Cecil;

class Program
{
    static int Main(string[] args)
    {
        string path = null;
        string pattern = null;
        string file = null;

        foreach (var arg in args)
        {
            if (Directory.Exists(arg))
            {
                path = Path.GetFullPath(arg);
            }
            else if (
                arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                pattern = arg;
                if (File.Exists(pattern))
                {
                    file = Path.GetFullPath(pattern);
                }
            }
            else
            {
                Console.Error.WriteLine("Unknown argument: " + arg);
                return 1;
            }
        }

        if (file != null)
        {
            Dump(file);
            return 0;
        }

        if (pattern != null)
        {
            path = path ?? Environment.CurrentDirectory;
        }
        else if (path != null)
        {
            pattern = "*.dll";
        }

        if (path != null && pattern != null)
        {
            var dlls = Directory.GetFiles(path, pattern);
            foreach (var dll in dlls)
            {
                Dump(dll);
            }

            return 0;
        }

        Console.Error.WriteLine("Usage: DumpAssembly.exe [<path>] [<pattern>] | [<dllPath>]");
        return 2;
    }

    private static void Dump(string dll)
    {
        try
        {
            var assembly = ModuleDefinition.ReadModule(dll);
            Console.WriteLine(dll);

            var types = assembly.Types;
            foreach (var type in types)
            {
                Console.WriteLine("    " + type.FullName);
            }
        }
        catch
        {
        }
    }
}