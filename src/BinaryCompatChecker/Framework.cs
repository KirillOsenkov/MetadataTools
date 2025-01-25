using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace BinaryCompatChecker;

public class Framework
{
    private static readonly Dictionary<string, bool> frameworkAssemblyNames = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> frameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accessibility",
        "Microsoft.CSharp",
        "Microsoft.VisualBasic",
        "Microsoft.VisualC",
        "Microsoft.Win32.Primitives",
        "Microsoft.WindowsCE.Forms",
        "mscorlib",
        "netstandard",
        "PresentationCore",
        "PresentationFramework",
        "ReachFramework",
        "System",
        "UIAutomationClient",
        "UIAutomationProvider",
        "UIAutomationTypes",
        "WindowsBase",
        "WindowsFormsIntegration"
    };

    public static string DotnetRuntimeDirectory { get; } = Path.GetDirectoryName(typeof(object).Assembly.Location);
    private static readonly string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly string desktopNetFrameworkDirectory = Path.Combine(windowsDirectory, "Microsoft.NET");

    private static readonly List<string> desktopNetFrameworkDirectories = new List<string>
    {
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_MSIL"),
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_32"),
        Path.Combine(desktopNetFrameworkDirectory, "assembly", "GAC_64"),
        Path.Combine(windowsDirectory, "assembly", "GAC_MSIL"),
        Path.Combine(windowsDirectory, "assembly", "GAC_32"),
        Path.Combine(windowsDirectory, "assembly", "GAC_64"),
        Path.Combine(windowsDirectory, "assembly", "GAC"),
    };

    public static IEnumerable<string> DesktopNetFrameworkDirectories => desktopNetFrameworkDirectories;

    public static string MscorlibFilePath { get; } = Path.Combine(desktopNetFrameworkDirectory, "Framework", "v4.0.30319", "mscorlib.dll");

    public static Version TryGetFrameworkRedirect(string name)
    {
        lock (frameworkRedirects)
        {
            frameworkRedirects.TryGetValue(name, out var frameworkRedirectVersion);
            return frameworkRedirectVersion;
        }
    }

    public static bool IsFrameworkName(string shortName)
    {
        return
            shortName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            frameworkNames.Contains(shortName);
    }

    public static bool IsFrameworkRedirect(string shortName)
    {
        lock (frameworkRedirects)
        {
            return frameworkRedirects.ContainsKey(shortName);
        }
    }

    public static bool IsRoslynAssembly(string assemblyName)
    {
        if (assemblyName.Contains("Microsoft.CodeAnalysis") || assemblyName.Contains("VisualStudio.LanguageServices"))
        {
            return true;
        }

        return false;
    }

    public static bool IsNetFrameworkAssembly(string assemblyName)
    {
        lock (frameworkAssemblyNames)
        {
            frameworkAssemblyNames.TryGetValue(assemblyName, out bool result);
            return result;
        }
    }

    /// <summary>
    /// Returns true if the <paramref name="assembly"/> is .NET Framework assembly.
    /// </summary>
    public static bool IsNetFrameworkAssembly(AssemblyDefinition assembly)
    {
        lock (frameworkAssemblyNames)
        {
            string key = assembly.MainModule.FileName;
            if (frameworkAssemblyNames.TryGetValue(key, out bool result))
            {
                return result;
            }

            // Hacky way of detecting it.
            result = assembly
                .CustomAttributes
                .FirstOrDefault(a => IsAssemblyProductFramework(a) || IsAssemblyMetadataFramework(a)) != null;
            frameworkAssemblyNames[key] = result;
            return result;
        }
    }

    private static bool IsAssemblyMetadataFramework(CustomAttribute a)
    {
        return
            a.AttributeType.Name == "AssemblyMetadataAttribute" &&
            a.ConstructorArguments != null &&
            a.ConstructorArguments.Count > 0 &&
            a.ConstructorArguments[0].Value.ToString() == ".NETFrameworkAssembly";
    }

    private static bool IsAssemblyProductFramework(CustomAttribute a)
    {
        return
            a.AttributeType.Name == "AssemblyProductAttribute" &&
            a.ConstructorArguments != null &&
            a.ConstructorArguments.FirstOrDefault(c =>
                c.Value.ToString() == "Microsoft® .NET Framework" ||
                c.Value.ToString() == "Microsoft® .NET").Value != null;
    }

    /// <summary>
    /// Returns true if the <paramref name="assembly"/> is a facade assembly with type forwarders only.
    /// </summary>
    public static bool IsFacadeAssembly(AssemblyDefinition assembly)
    {
        var types = assembly.MainModule.Types;
        if (types.Count == 1 && types[0].FullName == "<Module>" && assembly.MainModule.HasExportedTypes)
        {
            return true;
        }

        return false;
    }

    private static readonly Version LessThan4100 = new Version("4.0.99.99");
    private static readonly Version LessThan4200 = new Version("4.1.99.99");
    private static readonly Version LessThan4300 = new Version("4.2.99.99");

    private static readonly Dictionary<string, Version> frameworkRedirects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.VisualBasic"] = new Version("7.0.5500.0"),
        ["Microsoft.WindowsCE.Forms"] = new Version("1.0.5500.0"),
        ["System"] = new Version("1.0.5500.0"),
        ["System.AppContext"] = LessThan4200,
        ["System.Collections"] = LessThan4100,
        ["System.Collections.Concurrent"] = LessThan4100,
        ["System.Collections.NonGeneric"] = LessThan4100,
        ["System.Collections.Specialized"] = LessThan4100,
        ["System.ComponentModel"] = LessThan4100,
        ["System.ComponentModel.Annotations"] = LessThan4100,
        ["System.ComponentModel.EventBasedAsync"] = LessThan4100,
        ["System.ComponentModel.Primitives"] = LessThan4200,
        ["System.ComponentModel.TypeConverter"] = LessThan4200,
        ["System.Console"] = LessThan4100,
        ["System.Data"] = new Version("1.0.5500.0"),
        ["System.Data.Common"] = LessThan4300,
        ["System.Diagnostics.Contracts"] = LessThan4100,
        ["System.Diagnostics.Debug"] = LessThan4100,
        ["System.Diagnostics.FileVersionInfo"] = LessThan4100,
        ["System.Diagnostics.Process"] = LessThan4200,
        ["System.Diagnostics.StackTrace"] = LessThan4200,
        ["System.Diagnostics.TextWriterTraceListener"] = LessThan4100,
        ["System.Diagnostics.Tools"] = LessThan4100,
        ["System.Diagnostics.TraceSource"] = LessThan4100,
        ["System.Diagnostics.Tracing"] = LessThan4300,
        ["System.Drawing"] = new Version("1.0.5500.0"),
        ["System.Drawing.Primitives"] = LessThan4100,
        ["System.Dynamic.Runtime"] = LessThan4100,
        ["System.Globalization"] = LessThan4100,
        ["System.Globalization.Calendars"] = LessThan4100,
        ["System.Globalization.Extensions"] = LessThan4200,
        ["System.IO"] = LessThan4200,
        ["System.IO.Compression"] = LessThan4300,
        ["System.IO.Compression.ZipFile"] = LessThan4100,
        ["System.IO.FileSystem"] = LessThan4100,
        ["System.IO.FileSystem.DriveInfo"] = LessThan4100,
        ["System.IO.FileSystem.Primitives"] = LessThan4100,
        ["System.IO.FileSystem.Watcher"] = LessThan4100,
        ["System.IO.IsolatedStorage"] = LessThan4100,
        ["System.IO.MemoryMappedFiles"] = LessThan4100,
        ["System.IO.Pipes"] = LessThan4100,
        ["System.IO.UnmanagedMemoryStream"] = LessThan4100,
        ["System.Linq"] = LessThan4200,
        ["System.Linq.Expressions"] = LessThan4200,
        ["System.Linq.Parallel"] = LessThan4100,
        ["System.Linq.Queryable"] = LessThan4100,
        ["System.Net.Http"] = LessThan4300,
        ["System.Net.Http.Rtc"] = LessThan4100,
        ["System.Net.NameResolution"] = LessThan4100,
        ["System.Net.NetworkInformation"] = LessThan4200,
        ["System.Net.Ping"] = LessThan4100,
        ["System.Net.Primitives"] = LessThan4100,
        ["System.Net.Requests"] = LessThan4100,
        ["System.Net.Security"] = LessThan4100,
        ["System.Net.Sockets"] = LessThan4300,
        ["System.Net.WebHeaderCollection"] = LessThan4100,
        ["System.Net.WebSockets"] = LessThan4100,
        ["System.Net.WebSockets.Client"] = LessThan4100,
        ["System.ObjectModel"] = LessThan4100,
        ["System.Reflection"] = LessThan4200,
        ["System.Reflection.Emit"] = LessThan4100,
        ["System.Reflection.Emit.ILGeneration"] = LessThan4100,
        ["System.Reflection.Emit.Lightweight"] = LessThan4100,
        ["System.Reflection.Extensions"] = LessThan4100,
        ["System.Reflection.Primitives"] = LessThan4100,
        ["System.Resources.Reader"] = LessThan4100,
        ["System.Resources.ResourceManager"] = LessThan4100,
        ["System.Resources.Writer"] = LessThan4100,
        ["System.Runtime"] = LessThan4200,
        ["System.Runtime.CompilerServices.VisualC"] = LessThan4100,
        ["System.Runtime.Extensions"] = LessThan4200,
        ["System.Runtime.InteropServices"] = LessThan4200,
        ["System.Runtime.Handles"] = LessThan4100,
        ["System.Runtime.InteropServices.RuntimeInformation"] = LessThan4100,
        ["System.Runtime.InteropServices.WindowsRuntime"] = LessThan4100,
        ["System.Runtime.Numerics"] = LessThan4100,
        ["System.Runtime.Serialization.Formatters"] = LessThan4100,
        ["System.Runtime.Serialization.Json"] = LessThan4100,
        ["System.Runtime.Serialization.Primitives"] = LessThan4300,
        ["System.Runtime.Serialization.Xml"] = LessThan4200,
        ["System.Security.Claims"] = LessThan4100,
        ["System.Security.Cryptography.Algorithms"] = LessThan4300,
        ["System.Security.Cryptography.Csp"] = LessThan4100,
        ["System.Security.Cryptography.Encoding"] = LessThan4100,
        ["System.Security.Cryptography.Primitives"] = LessThan4100,
        ["System.Security.Cryptography.X509Certificates"] = LessThan4200,
        ["System.Security.Principal"] = LessThan4100,
        ["System.Security.SecureString"] = LessThan4200,
        ["System.ServiceModel.Duplex"] = LessThan4100,
        ["System.ServiceModel.Http"] = LessThan4100,
        ["System.ServiceModel.NetTcp"] = LessThan4100,
        ["System.ServiceModel.Primitives"] = LessThan4100,
        ["System.ServiceModel.Security"] = LessThan4100,
        ["System.Text.Encoding"] = LessThan4100,
        ["System.Text.Encoding.Extensions"] = LessThan4100,
        ["System.Text.RegularExpressions"] = LessThan4200,
        ["System.Threading"] = LessThan4100,
        ["System.Threading.Overlapped"] = LessThan4200,
        ["System.Threading.Tasks"] = LessThan4100,
        ["System.Threading.Tasks.Parallel"] = LessThan4100,
        ["System.Threading.Thread"] = LessThan4100,
        ["System.Threading.ThreadPool"] = LessThan4100,
        ["System.Threading.Timer"] = LessThan4100,
        ["System.ValueTuple"] = LessThan4100,
        ["System.Web.Services"] = new Version("1.0.5500.0"),
        ["System.Windows"] = LessThan4200,
        ["System.Windows.Forms"] = new Version("1.0.5500.0"),
        ["System.Xml"] = new Version("1.0.5500.0"),
        ["System.Xml.ReaderWriter"] = LessThan4200,
        ["System.Xml.Serialization"] = LessThan4200,
        ["System.Xml.XDocument"] = LessThan4100,
        ["System.Xml.XmlDocument"] = LessThan4100,
        ["System.Xml.XmlSerializer"] = LessThan4100,
        ["System.Xml.XPath"] = LessThan4100,
        ["System.Xml.XPath.XDocument"] = LessThan4200,
    };
}