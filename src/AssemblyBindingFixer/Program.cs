using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace AssemblyBindingFixer
{
    public class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            FixerArguments arguments = new FixerArguments()
            {
                ApplicationPath = args[0]
            };

            foreach (var arg in args.Skip(1))
            {
                if (TryParse(arg, "redistList", out var value))
                {
                    arguments.FrameworkDefinitions.Add((value, FrameworkDefinitionFileKind.RedistList));
                }
                else if (TryParse(arg, "frameworkAssemblyList", out value))
                {
                    arguments.FrameworkDefinitions.Add((value, FrameworkDefinitionFileKind.SimpleNameList));
                }
            }

            if (!File.Exists(arguments.ApplicationPath))
            {
                Console.Error.WriteLine("Specified root file does not exist: " + arguments.ApplicationPath);
                return 1;
            }

            var result = Fixer.Fix(arguments);
            var succeeded = true;
            foreach (var diagnostic in result.Diagnostics.OrderBy(d => d.level == DiagnosticLevel.Error ? 1 : 0).ThenBy(d => d.message))
            {
                if (diagnostic.level == DiagnosticLevel.Error)
                {
                    Console.Error.WriteLine($"{diagnostic.level}: {diagnostic.message}");
                    succeeded = false;
                }
                else
                {
                    Console.WriteLine($"{diagnostic.level}: {diagnostic.message}");
                }
            }

            return succeeded ? 0 : 1;
        }

        private static bool TryParse(string argument, string argumentName, out string value)
        {
            string argumentPrefix = $"/{argumentName}:";
            if (argument.StartsWith(argumentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = argument.Substring(argumentPrefix.Length);
                return true;
            }

            value = null;
            return false;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage: AssemblyBindingFixer <root-binary> [options]
    <root-binary>: root binary for which to fix appconfig
    Options:
        /redistList:<path> - Specifies a redist file describing files in the target framework
        /frameworkAssemblyList:<path> - Specifies framework assemblies via a flat file with framework assembly names on each line");
        }
    }
}
